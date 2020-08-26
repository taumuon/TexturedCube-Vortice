using System;
using System.Linq;
using Vortice.Mathematics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VulkanCore.Samples
{
    internal class TextureData
    {
        public Mipmap[] Mipmaps { get; set; }
        public VkFormat Format { get; set; }

        public class Mipmap
        {
            public byte[] Data { get; set; }
            public Size3 Extent { get; set; }
            public int Size { get; set; }
        }
    }

    public unsafe class VulkanImage : IDisposable
    {
        private VulkanImage(VulkanContext ctx, VkImage image, VkDeviceMemory memory, VkImageView view, VkFormat format)
        {
            Image = image;
            Memory = memory;
            View = view;
            Format = format;
            this.ctx = ctx;
        }

        public VkFormat Format { get; }
        public VkImage Image { get; }
        public VkImageView View { get; }
        public VkDeviceMemory Memory { get; }

        private VulkanContext ctx;

        public const uint QueueFamilyIgnored = uint.MaxValue;
        public void Dispose()
        {
            vkDestroyImageView(ctx.Device, View, null);
            vkFreeMemory(ctx.Device, Memory, null);
            vkDestroyImage(ctx.Device, Image, null);
        }

        public static implicit operator VkImage(VulkanImage value) => value.Image;

        public static VulkanImage DepthStencil(VulkanContext device, int width, int height)
        {
            VkFormat[] validFormats =
            {
                VkFormat.D32SFloatS8UInt,
                VkFormat.D32SFloat,
                VkFormat.D24UNormS8UInt,
                VkFormat.D16UNormS8UInt,
                VkFormat.D16UNorm
            };

            VkFormat? potentialFormat = validFormats.FirstOrDefault(
                validFormat =>
                {
                    VkFormatProperties formatProps;
                    vkGetPhysicalDeviceFormatProperties(device.PhysicalDevice, validFormat, out formatProps);

                    return (formatProps.optimalTilingFeatures & VkFormatFeatureFlags.DepthStencilAttachment) > 0;
                });

            if (!potentialFormat.HasValue)
                throw new InvalidOperationException("Required depth stencil format not supported.");

            VkFormat format = potentialFormat.Value;

            VkImageCreateInfo imageCreateInfo = new VkImageCreateInfo
            {
                sType = VkStructureType.ImageCreateInfo,
                pNext = null,
                imageType = VkImageType.Image2D,
                format = format,
                extent = new Vortice.Mathematics.Size3 { Width = width, Height = height, Depth = 1 },
                mipLevels = 1,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.TransferSrc
            };

            VkImage image;
            VkResult result = vkCreateImage(device.Device, &imageCreateInfo, null, out image);
            result.CheckResult();

            VkMemoryRequirements memReq;
            vkGetImageMemoryRequirements(device.Device, image, out memReq);

            vkGetPhysicalDeviceMemoryProperties(device.PhysicalDevice, out VkPhysicalDeviceMemoryProperties memoryProperties);

            uint heapIndex = BufferHelper.GetMemoryTypeIndex(memReq.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal, memoryProperties);

            VkMemoryAllocateInfo memAllocInfo = new VkMemoryAllocateInfo()
            {
                sType = VkStructureType.MemoryAllocateInfo,
                pNext = null,
                allocationSize = memReq.size,
                memoryTypeIndex = heapIndex
            };

            VkDeviceMemory memory;
            result = vkAllocateMemory(device.Device, &memAllocInfo, null, &memory);
            result.CheckResult();

            result = vkBindImageMemory(device.Device, image, memory, 0);
            result.CheckResult();

            VkImageViewCreateInfo imageViewCreateInfo = new VkImageViewCreateInfo
            {
                sType = VkStructureType.ImageViewCreateInfo,
                pNext = null,
                format = format,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil, 0, 1, 0, 1),
                image = image,
                viewType = VkImageViewType.Image2D
            };

            VkImageView view;
            result = vkCreateImageView(device.Device, &imageViewCreateInfo, null, out view);
            result.CheckResult();

            return new VulkanImage(device, image, memory, view, format);
        }

        internal static VulkanImage Texture2D(VulkanContext ctx, TextureData tex2D)
        {
            ulong size = (ulong)tex2D.Mipmaps[0].Size;
            VkBuffer stagingBuffer;
            var bufferCreateInfo = new VkBufferCreateInfo
            {
                sType = VkStructureType.BufferCreateInfo,
                pNext = null,
                size = (ulong)tex2D.Mipmaps[0].Size,
                usage = VkBufferUsageFlags.TransferSrc
            };
            vkCreateBuffer(ctx.Device, &bufferCreateInfo, null, out stagingBuffer);

            vkGetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out VkPhysicalDeviceMemoryProperties memoryProperties);
            vkGetBufferMemoryRequirements(ctx.Device, stagingBuffer, out VkMemoryRequirements stagingMemReq);
            uint heapIndex = BufferHelper.GetMemoryTypeIndex(stagingMemReq.memoryTypeBits, VkMemoryPropertyFlags.HostVisible, memoryProperties);

            VkMemoryAllocateInfo memAllocInfo = new VkMemoryAllocateInfo()
            {
                sType = VkStructureType.MemoryAllocateInfo,
                pNext = null,
                allocationSize = stagingMemReq.size,
                memoryTypeIndex = heapIndex
            };

            VkDeviceMemory stagingMemory;
            VkResult result = vkAllocateMemory(ctx.Device, &memAllocInfo, null, &stagingMemory);
            result.CheckResult();

            result = vkBindBufferMemory(ctx.Device, stagingBuffer, stagingMemory, 0);
            result.CheckResult();

            void* vertexPtr;
            result = vkMapMemory(ctx.Device, stagingMemory, 0, (ulong)tex2D.Mipmaps[0].Size, 0, &vertexPtr);
            result.CheckResult();

            fixed (byte* dataPtr = &tex2D.Mipmaps[0].Data[0])
            {
                Buffer.MemoryCopy(dataPtr, vertexPtr, size, size);
            }

            vkUnmapMemory(ctx.Device, stagingMemory);

            // Setup buffer copy regions for each mip level.
            var bufferCopyRegions = new VkBufferImageCopy[tex2D.Mipmaps.Length]; // TODO: stackalloc
            int offset = 0;
            for (int i = 0; i < bufferCopyRegions.Length; i++)
            {
                // TODO: from VulkanCore, doesn't look correct (reassigns bufferCopyRegions in each loop)
                bufferCopyRegions = new[]
                {
                    new VkBufferImageCopy
                    {
                        imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, (uint)i, 0, 1),
                        imageExtent = tex2D.Mipmaps[0].Extent,
                        bufferOffset = (ulong)offset
                    }
                };
                offset += tex2D.Mipmaps[i].Size;
            }

            // Create optimal tiled target image.
            var createInfo = new VkImageCreateInfo
            {
                sType = VkStructureType.ImageCreateInfo,
                pNext = null,
                imageType = VkImageType.Image2D,
                format = tex2D.Format,
                mipLevels = (uint)tex2D.Mipmaps.Length,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                sharingMode = VkSharingMode.Exclusive,
                initialLayout = VkImageLayout.Undefined,
                extent = tex2D.Mipmaps[0].Extent,
                usage = VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst
            };

            VkImage image;
            result = vkCreateImage(ctx.Device, &createInfo, null, out image);
            result.CheckResult();

            VkMemoryRequirements imageMemReq;
            vkGetImageMemoryRequirements(ctx.Device, image, out imageMemReq);

            vkGetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out VkPhysicalDeviceMemoryProperties imageMemoryProperties);

            uint imageHeapIndex = BufferHelper.GetMemoryTypeIndex(imageMemReq.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal, imageMemoryProperties);

            var allocInfo = new VkMemoryAllocateInfo
            {
                sType = VkStructureType.MemoryAllocateInfo,
                pNext = null,
                allocationSize = imageMemReq.size,
                memoryTypeIndex = imageHeapIndex,
            };
            VkDeviceMemory memory;
            result = vkAllocateMemory(ctx.Device, &allocInfo, null, &memory);
            result.CheckResult();

            result = vkBindImageMemory(ctx.Device, image, memory, 0);
            result.CheckResult();

            var subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, (uint)tex2D.Mipmaps.Length, 0, 1);

            // Copy the data from staging buffers to device local buffers.
            var allocInfo2 = new VkCommandBufferAllocateInfo()
            {
                sType = VkStructureType.CommandBufferAllocateInfo,
                commandPool = ctx.GraphicsCommandPool,

                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };
            VkCommandBuffer cmdBuffer;
            vkAllocateCommandBuffers(ctx.Device, &allocInfo2, &cmdBuffer);

            VkCommandBufferBeginInfo beginInfo = new VkCommandBufferBeginInfo()
            {
                sType = VkStructureType.CommandBufferBeginInfo,
                flags = VkCommandBufferUsageFlags.OneTimeSubmit,
            };

            vkBeginCommandBuffer(cmdBuffer, &beginInfo);

            VkImageMemoryBarrier imageMemoryBarrier = new VkImageMemoryBarrier
            {
                sType = VkStructureType.ImageMemoryBarrier,
                pNext = null,
                image = image,
                subresourceRange = subresourceRange,
                srcAccessMask = 0,
                dstAccessMask = VkAccessFlags.TransferWrite,
                oldLayout = VkImageLayout.Undefined,
                newLayout = VkImageLayout.TransferDstOptimal,
                srcQueueFamilyIndex = QueueFamilyIgnored,
                dstQueueFamilyIndex = QueueFamilyIgnored
            };

            vkCmdPipelineBarrier(cmdBuffer, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.Transfer, VkDependencyFlags.None, 0, null, 0, null, 1, &imageMemoryBarrier);

            fixed (VkBufferImageCopy* regionsPtr = bufferCopyRegions)
            {
                vkCmdCopyBufferToImage(cmdBuffer, stagingBuffer, image, VkImageLayout.TransferDstOptimal, (uint)bufferCopyRegions.Length, regionsPtr);
            }

            VkImageMemoryBarrier imageMemoryBarrier2 = new VkImageMemoryBarrier
            {
                sType = VkStructureType.ImageMemoryBarrier,
                pNext = null,
                image = image,
                subresourceRange = subresourceRange,
                srcAccessMask = VkAccessFlags.TransferWrite,
                dstAccessMask = VkAccessFlags.ShaderRead,
                oldLayout = VkImageLayout.TransferDstOptimal,
                newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                srcQueueFamilyIndex = (uint)QueueFamilyIgnored,
                dstQueueFamilyIndex = (uint)QueueFamilyIgnored
            };

            vkCmdPipelineBarrier(cmdBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.FragmentShader, VkDependencyFlags.None, 0, null, 0, null, 1, &imageMemoryBarrier2);

            vkEndCommandBuffer(cmdBuffer);

            // Submit.
            VkFenceCreateInfo fenceCreateInfo = new VkFenceCreateInfo
            {
                sType = VkStructureType.FenceCreateInfo,
                pNext = null
            };
            VkFence fence;
            result = vkCreateFence(ctx.Device, &fenceCreateInfo, null, out fence);
            result.CheckResult();

            var submitInfo = new VkSubmitInfo
            {
                sType = VkStructureType.SubmitInfo,
                pNext = null,
                commandBufferCount = 1,
                pCommandBuffers = &cmdBuffer
            };

            vkQueueSubmit(ctx.GraphicsQueue, submitInfo, fence);

            result = vkWaitForFences(ctx.Device, 1, &fence, false, ulong.MaxValue);
            result.CheckResult();

            // Cleanup staging resources.
            vkDestroyFence(ctx.Device, fence, null);
            vkFreeMemory(ctx.Device, stagingMemory, null);
            vkDestroyBuffer(ctx.Device, stagingBuffer, null);

            // Create image view.
            VkImageViewCreateInfo imageViewCreateInfo = new VkImageViewCreateInfo()
            {
                sType = VkStructureType.ImageViewCreateInfo,
                image = image,
                viewType = VkImageViewType.Image2D,
                format = tex2D.Format,
                subresourceRange = subresourceRange
            };

            VkImageView view;
            vkCreateImageView(ctx.Device, &imageViewCreateInfo, null, out view);

            return new VulkanImage(ctx, image, memory, view, tex2D.Format);
        }
    }
}
