using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using static VulkanCore.Samples.ContentLoaders.Loader;

namespace VulkanCore.Samples
{
    public unsafe class ContentManager : IDisposable
    {
        private readonly IVulkanAppHost _host;
        private readonly VulkanContext _ctx;
        private readonly string _contentRoot;
        private readonly Dictionary<string, VkShaderModule> _cachedShaderModule = new Dictionary<string, VkShaderModule>();
        private readonly Dictionary<string, VulkanImage> _cachedVulkanImage = new Dictionary<string, VulkanImage>();

        public ContentManager(IVulkanAppHost host, VulkanContext ctx, string contentRoot)
        {
            _host = host;
            _ctx = ctx;
            _contentRoot = contentRoot;
        }

        public VkShaderModule LoadShader(string contentName)
        {
            if (_cachedShaderModule.TryGetValue(contentName, out VkShaderModule value))
                return value;

            string path = Path.Combine(_contentRoot, contentName);

            value = LoadShaderModule(_host, _ctx, path);

            if (value == null)
                throw new NotImplementedException("Content type or extension not implemented.");

            _cachedShaderModule.Add(contentName, value);
            return value;
        }

        public VulkanImage LoadVulkanImage(string contentName)
        {
            if (_cachedVulkanImage.TryGetValue(contentName, out VulkanImage value))
                return value;

            string path = Path.Combine(_contentRoot, contentName);
            string extension = Path.GetExtension(path);

            if (extension.Equals(".ktx", StringComparison.OrdinalIgnoreCase))
            {
                value = LoadKtxVulkanImage(_host, _ctx, path);
            }

            if (value == null)
                throw new NotImplementedException("Content type or extension not implemented.");

            _cachedVulkanImage.Add(contentName, value);
            return value;
        }

        public void Dispose()
        {
            foreach (VkShaderModule value in _cachedShaderModule.Values)
            {
                vkDestroyShaderModule(_ctx.Device, value, null);
            }

            foreach (VulkanImage value in _cachedVulkanImage.Values)
            {
                value.Dispose();
            }

            _cachedShaderModule.Clear();
            _cachedVulkanImage.Clear();
        }
    }
}
