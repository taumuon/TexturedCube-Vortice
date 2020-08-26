using System;
using System.Runtime.CompilerServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VulkanCore.Samples
{
    public unsafe class VulkanBuffer : IDisposable
    {
        private VkDevice device;
        private VkCommandPool commandPool;

        private VulkanBuffer(VkDevice device, VkCommandPool commandPool, VkBuffer buffer, VkDeviceMemory memory, int count)
        {
            Buffer = buffer;
            Memory = memory;
            Count = count;
            this.device = device;
            this.commandPool = commandPool;
        }

        public VkBuffer Buffer { get; }
        public VkDeviceMemory Memory { get; }
        public int Count { get; }

        public void Dispose()
        {
            vkFreeMemory(device, Memory, null);
            vkDestroyBuffer(device, Buffer, null);
        }

        public static implicit operator VkBuffer(VulkanBuffer value) => value.Buffer;

        public static VulkanBuffer DynamicUniform<T>(VulkanContext ctx, int count) where T : struct
        {
            long size = Unsafe.SizeOf<T>() * count;

            VkBufferCreateInfo createInfo = new VkBufferCreateInfo
            {
                sType = VkStructureType.BufferCreateInfo,
                pNext = null,
                size = (ulong)size,
                usage = VkBufferUsageFlags.UniformBuffer
            };

            VkBuffer buffer;
            VkResult result = vkCreateBuffer(ctx.Device, &createInfo, null, out buffer);
            result.CheckResult();

            VkMemoryRequirements memoryRequirements;
            vkGetBufferMemoryRequirements(ctx.Device, buffer, out memoryRequirements);

            vkGetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out VkPhysicalDeviceMemoryProperties memoryProperties);

            uint memoryTypeIndex = BufferHelper.GetMemoryTypeIndex(memoryRequirements.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, memoryProperties);

            // We require host visible memory so we can map it and write to it directly.
            // We require host coherent memory so that writes are visible to the GPU right after unmapping it.

            VkMemoryAllocateInfo memAllocInfo = new VkMemoryAllocateInfo()
            {
                sType = VkStructureType.MemoryAllocateInfo,
                pNext = null,
                allocationSize = memoryRequirements.size,
                memoryTypeIndex = memoryTypeIndex
            };

            VkDeviceMemory memory;
            result = vkAllocateMemory(ctx.Device, &memAllocInfo, null, &memory);
            result.CheckResult();

            result = vkBindBufferMemory(ctx.Device, buffer, memory, 0);
            result.CheckResult();

            return new VulkanBuffer(ctx.Device, ctx.GraphicsCommandPool, buffer, memory, count);
        }

        public static VulkanBuffer Index(VulkanContext ctx, int[] indices)
        {
            return GetBuffer(ctx, indices, VkBufferUsageFlags.IndexBuffer);
        }

        public static VulkanBuffer Vertex<T>(VulkanContext ctx, T[] vertices) where T : unmanaged
        {
            return GetBuffer<T>(ctx, vertices, VkBufferUsageFlags.VertexBuffer);
        }

        public static VulkanBuffer Storage<T>(VulkanContext ctx, T[] data) where T : unmanaged
        {
            return GetBuffer<T>(ctx, data, VkBufferUsageFlags.VertexBuffer | VkBufferUsageFlags.StorageBuffer);
        }

        private static VulkanBuffer GetBuffer<T>(VulkanContext ctx, T[] data, VkBufferUsageFlags usage) where T : unmanaged
        {
            long size = data.Length * Unsafe.SizeOf<T>();

            // Create a staging buffer that is writable by host.
            var stagingCreateInfo = new VkBufferCreateInfo
            {
                sType = VkStructureType.BufferCreateInfo,
                pNext = null,
                usage = VkBufferUsageFlags.TransferSrc,
                sharingMode = VkSharingMode.Exclusive,
                size = (ulong)size
            };

            VkBuffer stagingBuffer;
            VkResult result = vkCreateBuffer(
                ctx.Device,
                &stagingCreateInfo,
                null,
                out stagingBuffer);
            result.CheckResult();

            VkMemoryRequirements stagingReq;
            vkGetBufferMemoryRequirements(ctx.Device, stagingBuffer, out stagingReq);

            vkGetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out VkPhysicalDeviceMemoryProperties memoryProperties);

            uint stagingMemoryTypeIndex = BufferHelper.GetMemoryTypeIndex(stagingReq.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, memoryProperties);

            VkMemoryAllocateInfo allocateInfo = new VkMemoryAllocateInfo
            {
                sType = VkStructureType.MemoryAllocateInfo,
                pNext = null,
                allocationSize = stagingReq.size,
                memoryTypeIndex = stagingMemoryTypeIndex
            };

            VkDeviceMemory stagingMemory;
            result = vkAllocateMemory(ctx.Device, &allocateInfo, null, &stagingMemory);
            result.CheckResult();

            void* vertexPtr;
            result = vkMapMemory(ctx.Device, stagingMemory, 0, (ulong)stagingReq.size, 0, &vertexPtr);
            result.CheckResult();

            fixed (T* dataPtr = &data[0])
            {
                System.Buffer.MemoryCopy(dataPtr, vertexPtr, size, size);
            }

            vkUnmapMemory(ctx.Device, stagingMemory);

            result = vkBindBufferMemory(ctx.Device, stagingBuffer, stagingMemory, 0);

            // Create a device local buffer where the data will be copied and which will be used for rendering.
            var bufferCreateInfo = new VkBufferCreateInfo
            {
                sType = VkStructureType.BufferCreateInfo,
                pNext = null,
                usage = usage | VkBufferUsageFlags.TransferDst,
                sharingMode = VkSharingMode.Exclusive,
                size = (ulong)size
            };

            VkBuffer buffer;
            result = vkCreateBuffer(
                ctx.Device,
                &bufferCreateInfo,
                null,
                out buffer);
            result.CheckResult();

            VkMemoryRequirements req;
            vkGetBufferMemoryRequirements(ctx.Device, buffer, out req);

            vkGetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out VkPhysicalDeviceMemoryProperties memProps);

            uint memoryTypeIndex = BufferHelper.GetMemoryTypeIndex(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal, memProps);

            VkMemoryAllocateInfo bufferAllocInfo = new VkMemoryAllocateInfo
            {
                sType = VkStructureType.MemoryAllocateInfo,
                pNext = null,
                allocationSize = req.size,
                memoryTypeIndex = memoryTypeIndex
            };

            VkDeviceMemory memory;
            result = vkAllocateMemory(ctx.Device, &bufferAllocInfo, null, &memory);
            result.CheckResult();

            result = vkBindBufferMemory(ctx.Device, buffer, memory, 0);

            // Copy the data from staging buffers to device local buffers.

            VkCommandBufferAllocateInfo allocInfo = new VkCommandBufferAllocateInfo()
            {
                sType = VkStructureType.CommandBufferAllocateInfo,
                commandPool = ctx.GraphicsCommandPool,

                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };

            VkCommandBuffer cmdBuffer;
            vkAllocateCommandBuffers(ctx.Device, &allocInfo, &cmdBuffer);

            VkCommandBufferBeginInfo beginInfo = new VkCommandBufferBeginInfo()
            {
                sType = VkStructureType.CommandBufferBeginInfo,
                flags = VkCommandBufferUsageFlags.OneTimeSubmit,
            };

            result = vkBeginCommandBuffer(cmdBuffer, &beginInfo);
            result.CheckResult();

            VkBufferCopy bufferCopy = new VkBufferCopy
            {
                size = (ulong)size
            };
            vkCmdCopyBuffer(cmdBuffer, stagingBuffer, buffer, 1, &bufferCopy);

            result = vkEndCommandBuffer(cmdBuffer);
            result.CheckResult();

            // Submit.
            VkFenceCreateInfo fenceCreateInfo = new VkFenceCreateInfo
            {
                sType = VkStructureType.FenceCreateInfo,
                pNext = null
            };

            VkFence fence;
            result = vkCreateFence(ctx.Device, &fenceCreateInfo, null, out fence);
            result.CheckResult();

            VkSubmitInfo submitInfo = new VkSubmitInfo
            {
                sType = VkStructureType.SubmitInfo,
                pNext = null,
                commandBufferCount = 1,
                pCommandBuffers = &cmdBuffer
            };

            result = vkQueueSubmit(ctx.GraphicsQueue, 1, &submitInfo, fence);

            result = vkWaitForFences(ctx.Device, 1, &fence, false, ulong.MaxValue);
            result.CheckResult();

            // Cleanup.
            vkDestroyFence(ctx.Device, fence, null);
            vkFreeCommandBuffers(ctx.Device, ctx.GraphicsCommandPool, 1, &cmdBuffer);
            vkDestroyBuffer(ctx.Device, stagingBuffer, null);
            vkFreeMemory(ctx.Device, stagingMemory, null);

            return new VulkanBuffer(ctx.Device, ctx.GraphicsCommandPool, buffer, memory, data.Length);
        }
    }

    public class BufferHelper
    {
        public static uint GetMemoryTypeIndex(uint typeBits, VkMemoryPropertyFlags properties, VkPhysicalDeviceMemoryProperties memoryProperties)
        {
            // Iterate over all memory types available for the Device used in this example
            for (uint i = 0; i < memoryProperties.memoryTypeCount; i++)
            {
                if ((typeBits & 1) == 1)
                {
                    if ((memoryProperties.GetMemoryType(i).propertyFlags & properties) == properties)
                    {
                        return i;
                    }
                }
                typeBits >>= 1;
            }

            throw new InvalidOperationException("Could not find a suitable memory type!");
        }
    }
}
