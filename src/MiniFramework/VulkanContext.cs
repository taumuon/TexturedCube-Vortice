using System;
using System.Collections.Generic;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VulkanCore.Samples
{
    /// <summary>
    /// Encapsulates Vulkan <see cref="PhysicalDevice"/> and <see cref="Device"/> and exposes queues
    /// and a command pool for rendering tasks.
    /// </summary>
    public unsafe class VulkanContext : IDisposable
    {
        public VulkanContext(VkInstance instance, VkSurfaceKHR surface, Platform platform)
        {
            // Find graphics and presentation capable physical device(s) that support
            // the provided surface for platform.
            int graphicsQueueFamilyIndex = -1;
            int computeQueueFamilyIndex = -1;
            int presentQueueFamilyIndex = -1;

            var physicalDevices = vkEnumeratePhysicalDevices(instance);
            foreach (var physicalDevice in physicalDevices)
            {
                uint Count = 0;

                vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &Count, null);
                VkQueueFamilyProperties* queueFamilyPropertiesptr = stackalloc VkQueueFamilyProperties[(int)Count];

                vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &Count, queueFamilyPropertiesptr);

                for (int i = 0; i < Count; i++)
                {
                    if (queueFamilyPropertiesptr[i].queueFlags.HasFlag(VkQueueFlags.Graphics))
                    {
                        if (graphicsQueueFamilyIndex == -1) graphicsQueueFamilyIndex = i;
                        if (computeQueueFamilyIndex == -1) computeQueueFamilyIndex = i;

                        VkBool32 isSupported;
                        uint queueFamilyIndex = (uint)i;
                        VkResult result = vkGetPhysicalDeviceSurfaceSupportKHR(physicalDevice, queueFamilyIndex, surface, out isSupported);
                        result.CheckResult();

                        if (isSupported == VkBool32.True)
                        {
                            bool presentationSupport = false;
                            if (platform == Platform.Win32)
                            {
                                presentationSupport = vkGetPhysicalDeviceWin32PresentationSupportKHR(physicalDevice, queueFamilyIndex);
                            }
                            else
                            {
                                presentationSupport = true;
                            }

                            if (presentationSupport)
                            {
                                presentQueueFamilyIndex = i;
                            }
                        }

                        if (graphicsQueueFamilyIndex != -1 &&
                            computeQueueFamilyIndex != -1 &&
                            presentQueueFamilyIndex != -1)
                        {
                            PhysicalDevice = physicalDevice;
                            break;
                        }
                    }
                }
                if (PhysicalDevice != null) break;
            }

            if (PhysicalDevice == null)
                throw new InvalidOperationException("No suitable physical device found.");

            vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out VkPhysicalDeviceMemoryProperties memoryProperties);
            MemoryProperties = memoryProperties;
            vkGetPhysicalDeviceFeatures(PhysicalDevice, out VkPhysicalDeviceFeatures features);
            Features = features;
            vkGetPhysicalDeviceProperties(PhysicalDevice, out VkPhysicalDeviceProperties physicalDeviceProperties);
            Properties = physicalDeviceProperties;

            // Create a logical device.
            bool sameGraphicsAndPresent = graphicsQueueFamilyIndex == presentQueueFamilyIndex;
            VkDeviceQueueCreateInfo* queueCreateInfos = stackalloc VkDeviceQueueCreateInfo[sameGraphicsAndPresent ? 1 : 2];

            float defaultQueuePriority = 1.0f;

            VkDeviceQueueCreateInfo queueInfoGraphics = new VkDeviceQueueCreateInfo
            {
                sType = VkStructureType.DeviceQueueCreateInfo,
                queueFamilyIndex = (uint)graphicsQueueFamilyIndex,
                queueCount = 1,
                pQueuePriorities = &defaultQueuePriority
            };

            queueCreateInfos[0] = queueInfoGraphics;

            if (!sameGraphicsAndPresent)
            {
                queueCreateInfos[1] = new VkDeviceQueueCreateInfo
                {
                    sType = VkStructureType.DeviceQueueCreateInfo,
                    queueFamilyIndex = (uint)presentQueueFamilyIndex,
                    queueCount = 1,
                    pQueuePriorities = &defaultQueuePriority
                };
            }

            VkDeviceCreateInfo deviceCreateInfo = new VkDeviceCreateInfo
            {
                sType = VkStructureType.DeviceCreateInfo,
                pNext = null,
                flags = VkDeviceCreateFlags.None,
                queueCreateInfoCount = (uint)(sameGraphicsAndPresent ? 1 : 2),
                pQueueCreateInfos = queueCreateInfos,
            };

            deviceCreateInfo.pEnabledFeatures = &features;

            string[] deviceExtensions = new[] {
                // If the device will be used for presenting to a display via a swapchain we need to request the swapchain extension
                "VK_KHR_swapchain"
            };

            deviceCreateInfo.enabledExtensionCount = (uint)deviceExtensions.Length;
            deviceCreateInfo.ppEnabledExtensionNames = Interop.String.AllocToPointers(deviceExtensions);

            VkResult result2 = vkCreateDevice(PhysicalDevice, &deviceCreateInfo, null, out VkDevice device);
            result2.CheckResult();

            Device = device;

            // Get queue(s).
            GraphicsQueue = GetQueue((uint)graphicsQueueFamilyIndex);
            ComputeQueue = computeQueueFamilyIndex == graphicsQueueFamilyIndex
                ? GraphicsQueue
                : GetQueue((uint)computeQueueFamilyIndex);
            PresentQueue = presentQueueFamilyIndex == graphicsQueueFamilyIndex
                ? GraphicsQueue
                : GetQueue((uint)presentQueueFamilyIndex);

            GraphicsQueueFamilyIndex = graphicsQueueFamilyIndex;
            PresentQueueFamilyIndex = presentQueueFamilyIndex;
            ComputeQueueFamilyIndex = presentQueueFamilyIndex;

            GraphicsCommandPool = CreateCommandPool((uint)graphicsQueueFamilyIndex);
            ComputeCommandPool = CreateCommandPool((uint)computeQueueFamilyIndex);
        }

        VkQueue GetQueue(uint familyIndex)
        {
            VkQueue queue;
            vkGetDeviceQueue(Device, familyIndex, 0, out queue);
            return queue;
        }

        // From Zeckoxe
        internal uint GetQueueFamilyIndex(VkQueueFlags queueFlags, List<VkQueueFamilyProperties> queueFamilyProperties)
        {
            // Dedicated queue for compute
            // Try to find a queue family index that supports compute but not graphics
            if ((queueFlags & VkQueueFlags.Compute) != 0)
            {
                for (uint i = 0; i < queueFamilyProperties.Count; i++)
                {
                    if (((queueFamilyProperties[(int)i].queueFlags & queueFlags) != 0) &&
                        (queueFamilyProperties[(int)i].queueFlags & VkQueueFlags.Graphics) == 0)
                    {
                        return i;
                    }
                }
            }


            // Dedicated queue for transfer
            // Try to find a queue family index that supports transfer but not graphics and compute
            if ((queueFlags & VkQueueFlags.Transfer) != 0)
            {
                for (uint i = 0; i < queueFamilyProperties.Count; i++)
                {
                    if (((queueFamilyProperties[(int)i].queueFlags & queueFlags) != 0) &&
                        (queueFamilyProperties[(int)i].queueFlags & VkQueueFlags.Graphics) == 0 &&
                        (queueFamilyProperties[(int)i].queueFlags & VkQueueFlags.Compute) == 0)
                    {
                        return i;
                    }
                }
            }

            // For other queue types or if no separate compute queue is present, return the first one to support the requested flags
            for (uint i = 0; i < queueFamilyProperties.Count; i++)
            {
                if ((queueFamilyProperties[(int)i].queueFlags & queueFlags) != 0)
                {
                    return i;
                }
            }

            throw new InvalidOperationException("Could not find a matching queue family index");
        }

        private VkCommandPool CreateCommandPool(uint queueFamilyIndex)
        {
            VkCommandPoolCreateInfo createInfo = new VkCommandPoolCreateInfo()
            {
                sType = VkStructureType.CommandPoolCreateInfo,
                queueFamilyIndex = queueFamilyIndex,
                flags = 0,
                pNext = null,
            };

            vkCreateCommandPool(Device, &createInfo, null, out VkCommandPool commandPool);

            return commandPool;
        }

        public VkPhysicalDevice PhysicalDevice { get; }
        public VkDevice Device { get; }
        public VkPhysicalDeviceMemoryProperties MemoryProperties { get; }
        public VkPhysicalDeviceFeatures Features { get; }
        public VkPhysicalDeviceProperties Properties { get; }


        public int GraphicsQueueFamilyIndex { get; }
        public int ComputeQueueFamilyIndex { get; }
        public int PresentQueueFamilyIndex { get; }

        public VkQueue GraphicsQueue { get; }
        public VkQueue ComputeQueue { get; }
        public VkQueue PresentQueue { get; }
        public VkCommandPool GraphicsCommandPool { get; }
        public VkCommandPool ComputeCommandPool { get; }

        public void Dispose()
        {
            vkDestroyCommandPool(Device, ComputeCommandPool, null);
            vkDestroyCommandPool(Device, GraphicsCommandPool, null);
            vkDestroyDevice(Device, null);
        }
    }
}
