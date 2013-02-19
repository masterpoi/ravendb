//-----------------------------------------------------------------------
// <copyright file="SimpleIndex.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Linq;
using Raven.Abstractions.Logging;
using Raven.Database.Extensions;
using Raven.Database.Linq;
using Raven.Database.Storage;

namespace Raven.Database.Indexing
{
	public class SimpleIndex : Index
	{
		public SimpleIndex(Directory directory, string name, IndexDefinition indexDefinition, AbstractViewGenerator viewGenerator, WorkContext context)
			: base(directory, name, indexDefinition, viewGenerator, context)
		{
		}

		public override bool IsMapReduce
		{
			get { return false; }
		}

		public DateTime LastCommitPointStoreTime { get; private set; }

		public override void IndexDocuments(AbstractViewGenerator viewGenerator, IndexingBatch batch, IStorageActionsAccessor actions, DateTime minimumTimestamp)
		{
			var count = 0;
			var sourceCount = 0;
			var sw = Stopwatch.StartNew();
			var start = SystemTime.UtcNow;
			Write((indexWriter, analyzer, stats) =>
			{
				var processedKeys = new HashSet<string>();
				var batchers = context.IndexUpdateTriggers.Select(x => x.CreateBatcher(name))
					.Where(x => x != null)
					.ToList();
				try
				{
					var docIdTerm = new Term(Constants.DocumentIdFieldName);
					var documentsWrapped = batch.Docs.Select((doc,i) =>
					{
						Interlocked.Increment(ref sourceCount);
						if (doc.__document_id == null)
							throw new ArgumentException(
								string.Format("Cannot index something which doesn't have a document id, but got: '{0}'", doc));

						string documentId = doc.__document_id.ToString();
						if (processedKeys.Add(documentId) == false)
							return doc;
						batchers.ApplyAndIgnoreAllErrors(
							exception =>
							{
								logIndexing.WarnException(
									string.Format("Error when executed OnIndexEntryDeleted trigger for index '{0}', key: '{1}'",
									              name, documentId),
									exception);
								context.AddError(name,
								                 documentId,
								                 exception.Message
									);
							},
							trigger => trigger.OnIndexEntryDeleted(documentId));
						if (batch.SkipDeleteFromIndex[i] == false || 
							context.ShouldRemoveFromIndex(documentId)) // maybe it is recently deleted?
							indexWriter.DeleteDocuments(docIdTerm.CreateTerm(documentId.ToLowerInvariant()));
				
						return doc;
					})
						.Where(x => x is FilteredDocument == false)
						.ToList();

					var allReferencedDocs = new ConcurrentQueue<IDictionary<string, HashSet<string>>>();
					BackgroundTaskExecuter.Instance.ExecuteAllBuffered(context, documentsWrapped, (partition) =>
					{
						var anonymousObjectToLuceneDocumentConverter = new AnonymousObjectToLuceneDocumentConverter(indexDefinition);
						var luceneDoc = new Document();
						var documentIdField = new Field(Constants.DocumentIdFieldName, "dummy", Field.Store.YES,
						                                Field.Index.NOT_ANALYZED_NO_NORMS);

						using (CurrentIndexingScope.Current = new CurrentIndexingScope(LoadDocument, allReferencedDocs.Enqueue))
						{
							foreach (var doc in RobustEnumerationIndex(partition, viewGenerator.MapDefinitions, stats))
							{
								float boost;
								var indexingResult = GetIndexingResult(doc, anonymousObjectToLuceneDocumentConverter, out boost);

								if (indexingResult.NewDocId != null && indexingResult.ShouldSkip == false)
								{
									Interlocked.Increment(ref count);
									luceneDoc.GetFields().Clear();
									luceneDoc.Boost = boost;
									documentIdField.SetValue(indexingResult.NewDocId.ToLowerInvariant());
									luceneDoc.Add(documentIdField);
									foreach (var field in indexingResult.Fields)
									{
										luceneDoc.Add(field);
									}
									batchers.ApplyAndIgnoreAllErrors(
										exception =>
										{
											logIndexing.WarnException(
												string.Format("Error when executed OnIndexEntryCreated trigger for index '{0}', key: '{1}'",
															  name, indexingResult.NewDocId),
												exception);
											context.AddError(name,
															 indexingResult.NewDocId,
															 exception.Message
												);
										},
										trigger => trigger.OnIndexEntryCreated(indexingResult.NewDocId, luceneDoc));
									LogIndexedDocument(indexingResult.NewDocId, luceneDoc);
									AddDocumentToIndex(indexWriter, luceneDoc, analyzer);
								}

								Interlocked.Increment(ref stats.IndexingSuccesses);
							}
						}
					});

					IDictionary<string, HashSet<string>> result;
					while (allReferencedDocs.TryDequeue(out result))
					{
						foreach (var referencedDocument in result)
						{
							actions.Indexing.UpdateDocumentReferences(name, referencedDocument.Key, referencedDocument.Value);
						}
					}

				}
				catch(Exception e)
				{
					batchers.ApplyAndIgnoreAllErrors(
						ex =>
						{
							logIndexing.WarnException("Failed to notify index update trigger batcher about an error", ex);
							context.AddError(name, null, ex.Message);
						},
						x => x.AnErrorOccured(e));
					throw;
				}
				finally
				{
					batchers.ApplyAndIgnoreAllErrors(
						e =>
						{
							logIndexing.WarnException("Failed to dispose on index update trigger", e);
							context.AddError(name, null, e.Message);
						},
						x => x.Dispose());
				}
				return new IndexedItemsInfo
				{
					ChangedDocs = sourceCount,
					HighestETag = batch.HighestEtagInBatch
				};
			});

			AddindexingPerformanceStat(new IndexingPerformanceStats
			{
				OutputCount = count,
				InputCount = sourceCount,
				Duration = sw.Elapsed,
				Operation = "Index",
				Started = start
			});
			logIndexing.Debug("Indexed {0} documents for {1}", count, name);
		}

		protected override void HandleCommitPoints(IndexedItemsInfo itemsInfo)
		{
			if (ShouldStoreCommitPoint() && itemsInfo.HighestETag != null)
			{
				context.IndexStorage.StoreCommitPoint(name, new IndexCommitPoint
				{
					HighestCommitedETag = itemsInfo.HighestETag,
					TimeStamp = LastIndexTime,
					SegmentsInfo = GetCurrentSegmentsInfo()
				});

				LastCommitPointStoreTime = SystemTime.UtcNow;
			}
			else if (itemsInfo.DeletedKeys != null)
			{
				context.IndexStorage.AddDeletedKeysToCommitPoints(name, itemsInfo.DeletedKeys);
			}
		}

		private IndexSegmentsInfo GetCurrentSegmentsInfo()
		{
			var segmentInfos = new SegmentInfos();
			var result = new IndexSegmentsInfo();

			try
			{
				segmentInfos.Read(directory);

				result.Generation = segmentInfos.Generation;
				result.SegmentsFileName = segmentInfos.GetCurrentSegmentFileName();
				result.ReferencedFiles = segmentInfos.Files(directory, false);
			}
			catch (CorruptIndexException ex)
			{
				logIndexing.WarnException(string.Format("Could not read segment information for an index '{0}'", name), ex);

				result.IsIndexCorrupted = true;
			}

			return result;
		}

		private bool ShouldStoreCommitPoint()
		{
			if (directory is RAMDirectory) // no point in trying to store commits for ram index
				return false;
					// no often than specified indexing interval
			return (LastIndexTime - PreviousIndexTime > context.Configuration.MinIndexingTimeIntervalToStoreCommitPoint ||
					// at least once for specified time interval
					LastIndexTime - LastCommitPointStoreTime > context.Configuration.MaxIndexCommitPointStoreTimeInterval); 
		}

		private IndexingResult GetIndexingResult(object doc, AnonymousObjectToLuceneDocumentConverter anonymousObjectToLuceneDocumentConverter, out float boost)
		{
			boost = 1;

			var boostedValue = doc as BoostedValue;
			if (boostedValue != null)
			{
				doc = boostedValue.Value;
				boost = boostedValue.Boost;
			}

			IndexingResult indexingResult;
			if (doc is DynamicJsonObject)
				indexingResult = ExtractIndexDataFromDocument(anonymousObjectToLuceneDocumentConverter, (DynamicJsonObject) doc);
			else
				indexingResult = ExtractIndexDataFromDocument(anonymousObjectToLuceneDocumentConverter, doc);

			if (Math.Abs(boost - 1) > float.Epsilon)
			{
				foreach (var abstractField in indexingResult.Fields)
				{
					abstractField.OmitNorms = false;
				}
			}

			return indexingResult;
		}

		private class IndexingResult
		{
			public string NewDocId;
			public List<AbstractField> Fields;
			public bool ShouldSkip;
		}

		private IndexingResult ExtractIndexDataFromDocument(AnonymousObjectToLuceneDocumentConverter anonymousObjectToLuceneDocumentConverter, DynamicJsonObject dynamicJsonObject)
		{
			var newDocId = dynamicJsonObject.GetRootParentOrSelf().GetDocumentId();
			return new IndexingResult
			{
				Fields = anonymousObjectToLuceneDocumentConverter.Index(((IDynamicJsonObject)dynamicJsonObject).Inner, Field.Store.NO).ToList(),
				NewDocId = newDocId is DynamicNullObject ? null : (string)newDocId,
				ShouldSkip = false
			};
		}

		private readonly ConcurrentDictionary<Type, PropertyDescriptorCollection> propertyDescriptorCache = new ConcurrentDictionary<Type, PropertyDescriptorCollection>();

		private IndexingResult ExtractIndexDataFromDocument(AnonymousObjectToLuceneDocumentConverter anonymousObjectToLuceneDocumentConverter, object doc)
		{
			Type type = doc.GetType();
			PropertyDescriptorCollection properties =
				propertyDescriptorCache.GetOrAdd(type, TypeDescriptor.GetProperties);
			
			var abstractFields = anonymousObjectToLuceneDocumentConverter.Index(doc, properties, Field.Store.NO).ToList();
			return new IndexingResult()
			{
				Fields = abstractFields,
				NewDocId = properties.Find(Constants.DocumentIdFieldName, false).GetValue(doc) as string,
				ShouldSkip = properties.Count > 1  // we always have at least __document_id
							&& abstractFields.Count == 0
			};
		}


		public override void Remove(string[] keys, WorkContext context)
		{
			Write((writer, analyzer,stats) =>
			{
				stats.Operation = IndexingWorkStats.Status.Ignore;
				logIndexing.Debug(() => string.Format("Deleting ({0}) from {1}", string.Join(", ", keys), name));
				var batchers = context.IndexUpdateTriggers.Select(x => x.CreateBatcher(name))
					.Where(x => x != null)
					.ToList();

				keys.Apply(
					key => batchers.ApplyAndIgnoreAllErrors(
						exception =>
						{
							logIndexing.WarnException(
								string.Format("Error when executed OnIndexEntryDeleted trigger for index '{0}', key: '{1}'",
								              name, key),
								exception);
							context.AddError(name, key, exception.Message);
						},
						trigger => trigger.OnIndexEntryDeleted(key)));
				writer.DeleteDocuments(keys.Select(k => new Term(Constants.DocumentIdFieldName, k.ToLowerInvariant())).ToArray());
				batchers.ApplyAndIgnoreAllErrors(
					e =>
					{
						logIndexing.WarnException("Failed to dispose on index update trigger", e);
						context.AddError(name, null, e.Message);
					},
					batcher => batcher.Dispose());

				IndexStats currentIndexStats = null;
				context.TransactionalStorage.Batch(accessor => currentIndexStats = accessor.Indexing.GetIndexStats(name));

				return new IndexedItemsInfo
				{
					ChangedDocs = keys.Length,
					HighestETag = currentIndexStats.LastIndexedEtag,
					DeletedKeys = keys
				};
			});
		}

		/// <summary>
		/// For index recovery purposes
		/// </summary>
		internal void RemoveDirectlyFromIndex(string[] keys)
		{
			Write((writer, analyzer, stats) =>
			{
				stats.Operation = IndexingWorkStats.Status.Ignore;

				writer.DeleteDocuments(keys.Select(k => new Term(Constants.DocumentIdFieldName, k.ToLowerInvariant())).ToArray());

				return new IndexedItemsInfo // just commit, don't create commit point and add any infor about deleted keys
				{
					ChangedDocs = keys.Length
				};
			});
		}
	}
}
