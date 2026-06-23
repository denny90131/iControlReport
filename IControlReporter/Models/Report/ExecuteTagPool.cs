using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IControlReporter.Models.Report
{
    public class ExecuteTagPool
    {
        public List<TagNode> TagPool { get; set; }

        public class TagNode
        {
            public string Name {get ; set;}
            public int Tag_ID { get; set; }
            public string Tag_Name { get; set; }
            public string Description { get; set; }
        }
    }
}