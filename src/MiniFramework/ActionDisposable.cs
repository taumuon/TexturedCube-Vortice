using System;
using System.Collections.Generic;
using System.Text;

namespace VulkanCore.Samples
{
    public class ActionDisposable : IDisposable
    {
        Action _dispose;

        public ActionDisposable(Action dispose)
        {
            _dispose = dispose;
        }
        public void Dispose()
        {
            _dispose();
        }
    }
}
