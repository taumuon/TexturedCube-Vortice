using System;
using System.IO;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VulkanCore.Samples.ContentLoaders
{
    internal static unsafe partial class Loader
    {
        public static VkShaderModule LoadShaderModule(IVulkanAppHost host, VulkanContext ctx, string path)
        {
            const int defaultBufferSize = 4096;
            using (Stream stream = host.Open(path))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms, defaultBufferSize);

                byte[] bytes = ms.ToArray();

                // Create a new shader module that will be used for Pipeline creation
                VkShaderModule shaderModule;
                vkCreateShaderModule(ctx.Device, bytes, null, out shaderModule);
                return shaderModule;
            }
        }
    }
}
