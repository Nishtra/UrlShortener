using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.Cosmos.Table;

namespace KossLinkShortener
{
    public class UrlKey : TableEntity
    {
        public int Id { get; set; }
    }


    public class UrlData : TableEntity
    {
        public string Url { get; set; }
        public int RequestCount { get; set; }
    }
}
