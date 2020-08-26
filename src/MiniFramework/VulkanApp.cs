using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VulkanCore.Samples
{
    public enum Platform
    {
        Android, Win32
    }

    public interface IVulkanAppHost : IDisposable
    {
        IntPtr WindowHandle { get; }
        IntPtr InstanceHandle { get; }
        int Width { get; }
        int Height { get; }
        Platform Platform { get; }

        Stream Open(string path);
    }

    public unsafe abstract class VulkanApp : IDisposable
    {
        private readonly Stack<IDisposable> _toDisposePermanent = new Stack<IDisposable>();
        private readonly Stack<IDisposable> _toDisposeFrame = new Stack<IDisposable>();
        private bool _initializingPermanent;

#if DEBUG
        private static bool EnableValidationLayers = true;
        private static readonly string[] s_RequestedValidationLayers = new[] { "VK_LAYER_KHRONOS_validation" };
#else
        private static bool EnableValidationLayers = false;
        private static readonly string[] s_RequestedValidationLayers = new string[0];
#endif

        private static void FindValidationLayers(List<string> appendTo)
        {
            var availableLayers = vkEnumerateInstanceLayerProperties();

            for (int i = 0; i < s_RequestedValidationLayers.Length; i++)
            {
                var hasLayer = false;
                for (int j = 0; j < availableLayers.Length; j++)
                    if (s_RequestedValidationLayers[i] == availableLayers[j].GetName())
                    {
                        hasLayer = true;
                        break;
                    }

                if (hasLayer)
                {
                    appendTo.Add(s_RequestedValidationLayers[i]);
                }
                else
                {
                    // TODO: Warn
                }
            }
        }

        public IVulkanAppHost Host { get; private set; }

        private vkDebugUtilsMessengerCallbackEXT _debugMessengerCallbackFunc;
        private VkDebugUtilsMessengerEXT debugMessenger = VkDebugUtilsMessengerEXT.Null;
        public VkInstance Instance { get; private set; }
        public VulkanContext Context { get; private set; }
        public ContentManager Content { get; private set; }

        protected VkSurfaceKHR Surface { get; private set; }
        protected VkSwapchainKHR Swapchain { get; private set; }
        protected VkFormat SwapchainFormat { get; private set; }
        protected VkImage[] SwapchainImages { get; private set; }
        protected VkCommandBuffer[] CommandBuffers { get; private set; }
        protected VkFence[] SubmitFences { get; private set; }

        protected VkSemaphore ImageAvailableSemaphore { get; private set; }
        protected VkSemaphore RenderingFinishedSemaphore { get; private set; }

        private VkSemaphore CreateSemaphore(VkDevice device)
        {
            VkSemaphoreCreateInfo createInfo = new VkSemaphoreCreateInfo
            {
                sType = VkStructureType.SemaphoreCreateInfo,
                pNext = null
            };

            VkSemaphore semaphore;
            VkResult result = vkCreateSemaphore(device, &createInfo, null, out semaphore);
            result.CheckResult();
            return semaphore;
        }

        public void Initialize(IVulkanAppHost host)
        {
            Host = host;
#if DEBUG
            const bool debug = true;
#else
            const bool debug = false;
#endif
            _initializingPermanent = true;

            VkResult result = vkInitialize();
            result.CheckResult();

            // Calling ToDispose here registers the resource to be automatically disposed on exit.
            Instance = CreateInstance(debug);
            Surface = CreateSurface();
            Context                    = new VulkanContext(Instance, Surface, Host.Platform);
            Content                    = new ContentManager(Host, Context, "Content");
            ImageAvailableSemaphore = CreateSemaphore(Context.Device);
            RenderingFinishedSemaphore = CreateSemaphore(Context.Device);

            _initializingPermanent = false;
            // Calling ToDispose here registers the resource to be automatically disposed on events
            // such as window resize.
            var swapchain = CreateSwapchain();
            Swapchain = swapchain;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroySwapchainKHR(Context.Device, swapchain, null);
            }));

            // Acquire underlying images of the freshly created swapchain.
            uint swapchainImageCount;
            result = vkGetSwapchainImagesKHR(Context.Device, Swapchain, &swapchainImageCount, null);
            result.CheckResult();

            var swapchainImages = stackalloc VkImage[(int)swapchainImageCount];
            result = vkGetSwapchainImagesKHR(Context.Device, Swapchain, &swapchainImageCount, swapchainImages);
            result.CheckResult();

            SwapchainImages = new VkImage[swapchainImageCount];
            for (int i = 0; i < swapchainImageCount; i++)
            {
                SwapchainImages[i] = swapchainImages[i];
            }

            VkCommandBufferAllocateInfo allocInfo = new VkCommandBufferAllocateInfo()
            {
                sType = VkStructureType.CommandBufferAllocateInfo,
                commandPool = Context.GraphicsCommandPool,

                level = VkCommandBufferLevel.Primary,
                commandBufferCount = (uint)SwapchainImages.Length,
            };

            VkCommandBuffer[] commandBuffers = new VkCommandBuffer[SwapchainImages.Length];
            fixed (VkCommandBuffer* commandBuffersPtr = &commandBuffers[0])
            {
                vkAllocateCommandBuffers(Context.Device, &allocInfo, commandBuffersPtr).CheckResult();
            }

            CommandBuffers = commandBuffers;

            // Create a fence for each commandbuffer so that we can wait before using it again
            _initializingPermanent = true; //We need our fences to be there permanently
            SubmitFences = new VkFence[SwapchainImages.Length];
            for (int i = 0; i < SubmitFences.Length; i++)
            {
                VkFenceCreateInfo fenceCreateInfo = new VkFenceCreateInfo()
                {
                    sType = VkStructureType.FenceCreateInfo,
                    pNext = null,
                    flags = VkFenceCreateFlags.Signaled
                };

                VkFence handle;

                vkCreateFence(Context.Device, &fenceCreateInfo, null, out handle);

                SubmitFences[i] = handle;
                ToDispose(new ActionDisposable(() =>
                {
                    vkDestroyFence(Context.Device, handle, null);
                }));
            }

            // Allow concrete samples to initialize their resources.
            InitializePermanent();
            _initializingPermanent = false;
            InitializeFrame();

            // Record commands for execution by Vulkan.
            RecordCommandBuffers();
        }

        /// <summary>
        /// Allows derived classes to initializes resources the will stay alive for the duration of
        /// the application.
        /// </summary>
        protected virtual void InitializePermanent() { }
        
        /// <summary>
        /// Allows derived classes to initializes resources that need to be recreated on events such
        /// as window resize.
        /// </summary>
        protected virtual void InitializeFrame() { }

        public void Resize()
        {
            vkDeviceWaitIdle(Context.Device).CheckResult();

            // Dispose all frame dependent resources.
            while (_toDisposeFrame.Count > 0)
                _toDisposeFrame.Pop().Dispose();

            // Reset all the command buffers allocated from the pools.
            vkResetCommandPool(Context.Device, Context.GraphicsCommandPool, VkCommandPoolResetFlags.None);
            vkResetCommandPool(Context.Device, Context.ComputeCommandPool, VkCommandPoolResetFlags.None);

            // Reinitialize frame dependent resources.
            var swapchain = CreateSwapchain();
            Swapchain = swapchain;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroySwapchainKHR(Context.Device, swapchain, null);
            }));
            SwapchainImages = GetSwapchainImages(Swapchain);
            InitializeFrame();

            // Re-record command buffers.
            RecordCommandBuffers();
        }

        private VkImage[] GetSwapchainImages(VkSwapchainKHR swapchain)
        {
            uint swapchainImageCount;
            VkResult result = vkGetSwapchainImagesKHR(Context.Device, swapchain, &swapchainImageCount, null);
            result.CheckResult();

            var swapchainImages = stackalloc VkImage[(int)swapchainImageCount];
            result = vkGetSwapchainImagesKHR(Context.Device, swapchain, &swapchainImageCount, swapchainImages);
            result.CheckResult();

            var images = new VkImage[swapchainImageCount];
            for (int i = 0; i < swapchainImageCount; i++)
                images[i] = swapchainImages[i];
            return images;
        }

        public void Tick(Timer timer)
        {
            Update(timer);
            Draw(timer);
        }

        protected virtual void Update(Timer timer) { }

        protected virtual void Draw(Timer timer)
        {
            // Acquire an index of drawing image for this frame.
            uint nextImageIndex;
            VkResult result = vkAcquireNextImageKHR(Context.Device, Swapchain, ulong.MaxValue, ImageAvailableSemaphore, VkFence.Null, out nextImageIndex);
            result.CheckResult();

            // Use a fence to wait until the command buffer has finished execution before using it again
            VkFence fence = SubmitFences[nextImageIndex];
            result = vkWaitForFences(Context.Device, 1, &fence, false, ulong.MaxValue);
            result.CheckResult();

            result = vkResetFences(Context.Device, 1, &fence);
            result.CheckResult();

            VkSemaphore signalSemaphore = RenderingFinishedSemaphore;
            VkSemaphore waitSemaphore = ImageAvailableSemaphore;
            VkPipelineStageFlags waitStages = VkPipelineStageFlags.ColorAttachmentOutput;
            VkCommandBuffer commandBuffer = CommandBuffers[nextImageIndex];

            VkSubmitInfo submitInfo = new VkSubmitInfo()
            {
                sType = VkStructureType.SubmitInfo,
                waitSemaphoreCount = 1,
                pWaitSemaphores = &waitSemaphore,
                pWaitDstStageMask = &waitStages,
                commandBufferCount = 1,
                pCommandBuffers = &commandBuffer,
                signalSemaphoreCount = 1,
                pSignalSemaphores = &signalSemaphore,
            };

            result = vkQueueSubmit(Context.GraphicsQueue, 1, &submitInfo, SubmitFences[nextImageIndex]);
            result.CheckResult();

            // Present the color output to screen.
            VkSemaphore waitSemaphoreHandle = RenderingFinishedSemaphore;
            VkSwapchainKHR swapchainHandle = Swapchain;
            var nativePresentInfo = new VkPresentInfoKHR
            {
                sType = VkStructureType.PresentInfoKHR,
                pNext = null,
                waitSemaphoreCount = 1,
                pWaitSemaphores = &waitSemaphoreHandle,
                swapchainCount = 1,
                pSwapchains = &swapchainHandle,
                pImageIndices = &nextImageIndex
            };

            result = vkQueuePresentKHR(Context.PresentQueue, &nativePresentInfo);
            result.CheckResult();
        }

        public virtual void Dispose()
        {
            vkDeviceWaitIdle(Context.Device).CheckResult();
            while (_toDisposeFrame.Count > 0)
                _toDisposeFrame.Pop().Dispose();
            while (_toDisposePermanent.Count > 0)
                _toDisposePermanent.Pop().Dispose();

            vkDestroySemaphore(Context.Device, RenderingFinishedSemaphore, null);
            vkDestroySemaphore(Context.Device, ImageAvailableSemaphore, null);
            Content.Dispose();
            Context.Dispose();
            vkDestroySurfaceKHR(Instance, Surface, null);
            if (debugMessenger != VkDebugUtilsMessengerEXT.Null)
            {
                vkDestroyDebugUtilsMessengerEXT(Instance, debugMessenger, null);
            }
            vkDestroyInstance(Instance, null);
        }

        private static readonly VkString s_EngineName = "TOOD Engine Name";

        private VkInstance CreateInstance(bool debug)
        {
            VkInstance instance;

            // Specify standard validation layers.
            string surfaceExtension;
            switch (Host.Platform)
            {
                case Platform.Android:
                    surfaceExtension = KHRAndroidSurfaceExtensionName;
                    break;
                case Platform.Win32:
                    surfaceExtension = KHRWin32SurfaceExtensionName;
                    break;
                default:
                    throw new NotImplementedException();
            }

            VkString name = "TODO Application Name";
            var appInfo = new VkApplicationInfo
            {
                sType = VkStructureType.ApplicationInfo,
                pApplicationName = name,
                applicationVersion = new VkVersion(1, 0, 0),
                pEngineName = s_EngineName,
                engineVersion = new VkVersion(1, 0, 0),
                apiVersion = VkVersion.Version_1_0,
            };

            var instanceExtensions = new List<string>
                {
                    KHRSurfaceExtensionName,
                    surfaceExtension
                };

            var instanceLayers = new List<string>();
            if (EnableValidationLayers)
            {
                FindValidationLayers(instanceLayers);
            }

            if (instanceLayers.Count > 0)
            {
                instanceExtensions.Add(EXTDebugUtilsExtensionName);
            }

            using var vkInstanceExtensions = new VkStringArray(instanceExtensions);
            var instanceCreateInfo = new VkInstanceCreateInfo
            {
                sType = VkStructureType.InstanceCreateInfo,
                pApplicationInfo = &appInfo,
                enabledExtensionCount = vkInstanceExtensions.Length,
                ppEnabledExtensionNames = vkInstanceExtensions
            };


            using var vkLayerNames = new VkStringArray(instanceLayers);
            if (instanceLayers.Count > 0)
            {
                instanceCreateInfo.enabledLayerCount = vkLayerNames.Length;
                instanceCreateInfo.ppEnabledLayerNames = vkLayerNames;
            }

            VkResult result = vkCreateInstance(&instanceCreateInfo, null, out instance);
            vkLoadInstance(instance);

            if (instanceLayers.Count > 0)
            {
                _debugMessengerCallbackFunc = DebugMessengerCallback;
                var debugCreateInfo = new VkDebugUtilsMessengerCreateInfoEXT
                {
                    sType = VkStructureType.DebugUtilsMessengerCreateInfoEXT,
                    messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.VerboseEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorEXT | VkDebugUtilsMessageSeverityFlagsEXT.WarningEXT,
                    messageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralEXT | VkDebugUtilsMessageTypeFlagsEXT.ValidationEXT | VkDebugUtilsMessageTypeFlagsEXT.PerformanceEXT,
                    pfnUserCallback = Marshal.GetFunctionPointerForDelegate(_debugMessengerCallbackFunc)
                };

                vkCreateDebugUtilsMessengerEXT(instance, &debugCreateInfo, null, out debugMessenger).CheckResult();
            }

            return instance;
        }

        private static VkBool32 DebugMessengerCallback(
                    VkDebugUtilsMessageSeverityFlagsEXT messageSeverity,
                    VkDebugUtilsMessageTypeFlagsEXT messageTypes,
                    VkDebugUtilsMessengerCallbackDataEXT* pCallbackData,
                    IntPtr userData)
        {
            var message = Vortice.Vulkan.Interop.GetString(pCallbackData->pMessage);
            if (messageTypes == VkDebugUtilsMessageTypeFlagsEXT.ValidationEXT)
            {
                Debug.WriteLine($"[Vulkan]: Validation: {messageSeverity} - {message}");
            }
            else
            {
                Debug.WriteLine($"[Vulkan]: {messageSeverity} - {message}");
            }

            return VkBool32.False;
        }

        private VkSurfaceKHR CreateSurface()
        {
            VkSurfaceKHR surface;
            VkResult result;
            // Create surface.
            switch (Host.Platform)
            {
                case Platform.Android:
                    var surfaceCreateInfoAndroid = new VkAndroidSurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.AndroidSurfaceCreateInfoKHR,
                        window = (IntPtr*)Host.WindowHandle.ToPointer()
                    };
                    result = vkCreateAndroidSurfaceKHR(Instance, &surfaceCreateInfoAndroid, null, out surface);
                    result.CheckResult();
                    return surface;
                case Platform.Win32:
                    var surfaceCreateInfoWin32 = new VkWin32SurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.Win32SurfaceCreateInfoKHR,
                        hinstance = Host.InstanceHandle,
                        hwnd = Host.WindowHandle
                    };
                    result = vkCreateWin32SurfaceKHR(Instance, &surfaceCreateInfoWin32, null, out surface);
                    result.CheckResult();
                    return surface;
                default:
                    throw new NotImplementedException();
            }
        }

        private VkSwapchainKHR CreateSwapchain()
        {
            VkSurfaceCapabilitiesKHR capabilities;
            vkGetPhysicalDeviceSurfaceCapabilitiesKHR(Context.PhysicalDevice, Surface, out capabilities).CheckResult();
            
            uint count;
            vkGetPhysicalDeviceSurfaceFormatsKHR(Context.PhysicalDevice, Surface, &count, null);

            VkSurfaceFormatKHR[] formats = new VkSurfaceFormatKHR[(int)count];
            fixed (VkSurfaceFormatKHR* formatsPtr = formats)
                vkGetPhysicalDeviceSurfaceFormatsKHR(Context.PhysicalDevice, Surface, &count, formatsPtr).CheckResult();

            vkGetPhysicalDeviceSurfacePresentModesKHR(Context.PhysicalDevice, Surface, &count, null).CheckResult();

            VkPresentModeKHR[] presentModes = new VkPresentModeKHR[count];
            fixed (VkPresentModeKHR* presentModesPtr = presentModes)
                vkGetPhysicalDeviceSurfacePresentModesKHR(Context.PhysicalDevice, Surface, &count, presentModesPtr).CheckResult();

            VkFormat format = formats.Length == 1 && formats[0].format == VkFormat.Undefined
                ? VkFormat.B8G8R8A8UNorm
                : formats[0].format;
            VkPresentModeKHR presentMode =
                presentModes.Contains(VkPresentModeKHR.MailboxKHR) ? VkPresentModeKHR.MailboxKHR :
                presentModes.Contains(VkPresentModeKHR.FifoRelaxedKHR) ? VkPresentModeKHR.FifoRelaxedKHR :
                presentModes.Contains(VkPresentModeKHR.FifoKHR) ? VkPresentModeKHR.FifoKHR :
                VkPresentModeKHR.ImmediateKHR;

            SwapChainSupportDetails swapChainSupport = QuerySwapChainSupport(Context.PhysicalDevice, Surface);
            VkSurfaceFormatKHR surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);

            uint imageCount = swapChainSupport.Capabilities.minImageCount + 1;
            if (swapChainSupport.Capabilities.maxImageCount > 0 &&
                imageCount > swapChainSupport.Capabilities.maxImageCount)
            {
                imageCount = swapChainSupport.Capabilities.maxImageCount;
            }

            SwapchainFormat = format;

            VkSwapchainCreateInfoKHR swapchainCI = new VkSwapchainCreateInfoKHR()
            {
                sType = VkStructureType.SwapchainCreateInfoKHR,
                pNext = null,
                surface = Surface,
                minImageCount = imageCount,
                imageFormat = format,
                imageColorSpace = surfaceFormat.colorSpace,
                imageExtent = capabilities.currentExtent,
                imageUsage = VkImageUsageFlags.ColorAttachment,
                preTransform = capabilities.currentTransform,
                imageArrayLayers = 1,
                imageSharingMode = VkSharingMode.Exclusive,
                queueFamilyIndexCount = 0,
                pQueueFamilyIndices = null,
                presentMode = presentMode,

                //oldSwapchain = SwapChain,

                // Setting clipped to VK_TRUE allows the implementation to discard rendering outside of the Surface area
                clipped = true,
                compositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueKHR,
            };
            VkSwapchainKHR SwapChain;

            vkCreateSwapchainKHR(Context.Device, &swapchainCI, null, out SwapChain).CheckResult();

            return SwapChain;
        }

        private static VkSurfaceFormatKHR ChooseSwapSurfaceFormat(ReadOnlySpan<VkSurfaceFormatKHR> availableFormats)
        {
            foreach (var availableFormat in availableFormats)
            {
                if (availableFormat.format == VkFormat.B8G8R8A8SRgb &&
                    availableFormat.colorSpace == VkColorSpaceKHR.SrgbNonlinearKHR)
                {
                    return availableFormat;
                }
            }

            return availableFormats[0];
        }

        private void RecordCommandBuffers()
        {
            VkImageSubresourceRange subresourceRange = new VkImageSubresourceRange()
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                baseArrayLayer = 0,
                layerCount = 1,
                levelCount = 1
            };

            for (int i = 0; i < CommandBuffers.Length; i++)
            {
                VkCommandBuffer cmdBuffer = CommandBuffers[i];

                VkCommandBufferBeginInfo beginInfo = new VkCommandBufferBeginInfo()
                {
                    sType = VkStructureType.CommandBufferBeginInfo,
                    flags = VkCommandBufferUsageFlags.SimultaneousUse
                };
                vkBeginCommandBuffer(cmdBuffer, &beginInfo);

                if (Context.PresentQueue != Context.GraphicsQueue)
                {
                    var barrierFromPresentToDraw = new VkImageMemoryBarrier(
                        SwapchainImages[i], subresourceRange,
                        VkAccessFlags.MemoryRead, VkAccessFlags.ColorAttachmentWrite,
                        VkImageLayout.Undefined, VkImageLayout.PresentSrcKHR,
                        (uint)Context.PresentQueueFamilyIndex, (uint)Context.GraphicsQueueFamilyIndex);

                    vkCmdPipelineBarrier(cmdBuffer,
                        VkPipelineStageFlags.ColorAttachmentOutput,
                        VkPipelineStageFlags.ColorAttachmentOutput,
                        0,
                        0, null, 0, null,
                        1, &barrierFromPresentToDraw
                        );
                }

                RecordCommandBuffer(cmdBuffer, i);

                if (Context.PresentQueue != Context.GraphicsQueue)
                {
                    var barrierFromDrawToPresent = new VkImageMemoryBarrier(
                        SwapchainImages[i], subresourceRange,
                        VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.MemoryRead,
                        VkImageLayout.PresentSrcKHR, VkImageLayout.PresentSrcKHR,
                        (uint)Context.GraphicsQueueFamilyIndex, (uint)Context.PresentQueueFamilyIndex);

                    vkCmdPipelineBarrier(cmdBuffer,
                        VkPipelineStageFlags.ColorAttachmentOutput,
                        VkPipelineStageFlags.BottomOfPipe,
                        0,
                        0, null, 0, null,
                        1, &barrierFromDrawToPresent
                        );
                }

                VkResult result = vkEndCommandBuffer(cmdBuffer);
                result.CheckResult();
            } 
        }
        
        protected abstract void RecordCommandBuffer(VkCommandBuffer cmdBuffer, int imageIndex);

        protected T ToDispose<T>(T disposable) where T : IDisposable
        {
            var toDispose = _initializingPermanent ? _toDisposePermanent : _toDisposeFrame;
            switch (disposable)
            {
                case IEnumerable<IDisposable> sequence:
                    foreach (var element in sequence)
                        toDispose.Push(element);
                    break;
                case IDisposable element:
                    toDispose.Push(element);
                    break;
            }
            return disposable;
        }
        private ref struct SwapChainSupportDetails
        {
            public VkSurfaceCapabilitiesKHR Capabilities;
            public ReadOnlySpan<VkSurfaceFormatKHR> Formats;
            public ReadOnlySpan<VkPresentModeKHR> PresentModes;
        };

        private static SwapChainSupportDetails QuerySwapChainSupport(VkPhysicalDevice device, VkSurfaceKHR surface)
        {
            SwapChainSupportDetails details = new SwapChainSupportDetails();
            vkGetPhysicalDeviceSurfaceCapabilitiesKHR(device, surface, out details.Capabilities).CheckResult();

            details.Formats = vkGetPhysicalDeviceSurfaceFormatsKHR(device, surface);
            details.PresentModes = vkGetPhysicalDeviceSurfacePresentModesKHR(device, surface);
            return details;
        }
    }
}
