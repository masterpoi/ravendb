﻿namespace Raven.Studio.Messages
{
	using Indexes.Browse;

	public class IndexChangeMessage
	{
		public EditIndexViewModel Index { get; set; }

		public bool IsRemoved { get; set; }
	}
}