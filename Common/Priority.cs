using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.Common
{
    public enum PriorityLevel
    {
        Heartbeat = 0, // 最高优先级
        Highest = 1,
        High = 2,
        Normal = 3,
        Low = 4,
        Lowest = 5 // 最低优先级
    }
}
