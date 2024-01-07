using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Interfaces;

namespace Persistence.Helpers
{
    public class Param
    {
        public int PageIndex { get; set; } = 0;
        public int PageSize { get; set; } = 30;
        public string Direction { get; set; }
        public string SortBy { get; set; }

    }


}