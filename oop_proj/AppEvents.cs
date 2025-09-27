using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace oop_proj
{
    internal class AppEvents
    {

        public static event Action<string>? OnStatusUpdate;

        public static void UpdateStatus(string text)
        {
            OnStatusUpdate?.Invoke(text);
        }
    }
}
