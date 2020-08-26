using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Mathematics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VulkanCore.Samples.TexturedCube
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WorldViewProjection
    {
        public Matrix4x4 World;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    public unsafe class TexturedCubeApp : VulkanApp
    {
        private VkRenderPass _renderPass;
        private VkImageView[] _imageViews;
        private VkFramebuffer[] _framebuffers;
        private VkPipelineLayout _pipelineLayout;
        private VkPipeline _pipeline;
        private VkDescriptorSetLayout _descriptorSetLayout;
        private VkDescriptorPool _descriptorPool;
        private VkDescriptorSet _descriptorSet;

        private VulkanImage _depthStencilBuffer;

        private VkSampler _sampler;
        private VulkanImage _cubeTexture;

        private VulkanBuffer _cubeVertices;
        private VulkanBuffer _cubeIndices;

        private VulkanBuffer _uniformBuffer;
        private WorldViewProjection _wvp;

        protected override void InitializePermanent()
        {
            var cube = GeometricPrimitive.Box(1.0f, 1.0f, 1.0f);

            _cubeTexture = Content.LoadVulkanImage("IndustryForgedDark512.ktx");
            _cubeVertices = ToDispose(VulkanBuffer.Vertex(Context, cube.Vertices));
            _cubeIndices = ToDispose(VulkanBuffer.Index(Context, cube.Indices));
            var sampler = CreateSampler();
            _sampler = sampler;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroySampler(Context.Device, sampler, null);
            }));
            _uniformBuffer = ToDispose(VulkanBuffer.DynamicUniform<WorldViewProjection>(Context, 1));
            var descriptorSetLayout = CreateDescriptorSetLayout();
            _descriptorSetLayout = descriptorSetLayout;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroyDescriptorSetLayout(Context.Device, descriptorSetLayout, null);
            }));
            var pipelineLayout = CreatePipelineLayout();
            _pipelineLayout = pipelineLayout;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroyPipelineLayout(Context.Device, pipelineLayout, null);
            }));
            var descriptorPool = CreateDescriptorPool();
            _descriptorPool = descriptorPool;
            ToDispose(new ActionDisposable(() => 
            {
                vkDestroyDescriptorPool(Context.Device, descriptorPool, null);
            }));
            _descriptorSet = CreateDescriptorSet(_descriptorPool); // Will be freed when pool is destroyed.
        }

        protected override void InitializeFrame()
        {
            _depthStencilBuffer = ToDispose(VulkanImage.DepthStencil(Context, Host.Width, Host.Height));
            var renderPass = CreateRenderPass();
            _renderPass = renderPass;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroyRenderPass(Context.Device, renderPass, null);
            }));
            var imageViews = CreateImageViews();
            _imageViews = imageViews;
            ToDispose(new ActionDisposable(() =>
            {
                foreach (var imageView in imageViews)
                {
                    vkDestroyImageView(Context.Device, imageView, null);
                }
            }));
            var frameBuffers = CreateFramebuffers();
            _framebuffers = frameBuffers;
            ToDispose(new ActionDisposable(() =>
            {
                foreach(var frameBuffer in frameBuffers)
                {
                    vkDestroyFramebuffer(Context.Device, frameBuffer, null);
                }
            }));
            var pipeline = CreateGraphicsPipeline();
            _pipeline = pipeline;
            ToDispose(new ActionDisposable(() =>
            {
                vkDestroyPipeline(Context.Device, pipeline, null);
            }));
            SetViewProjection();
        }

        protected override void Update(Timer timer)
        {
            const float twoPi = (float)Math.PI * 2.0f;
            const float yawSpeed = twoPi / 4.0f;
            const float pitchSpeed = 0.0f;
            const float rollSpeed = twoPi / 4.0f;

            _wvp.World = Matrix4x4.CreateFromYawPitchRoll(
                timer.TotalTime * yawSpeed % twoPi,
                timer.TotalTime * pitchSpeed % twoPi,
                timer.TotalTime * rollSpeed % twoPi);

            UpdateUniformBuffers();
        }

        protected override void RecordCommandBuffer(VkCommandBuffer cmdBuffer, int imageIndex)
        {
            VkClearValue* clearValues = stackalloc VkClearValue[2];
            clearValues[0] = 
                new VkClearValue
                {
                    color = new VkClearColorValue(0.39f, 0.58f, 0.93f, 1.0f)
                };
            clearValues[1]
                = new VkClearValue
                {
                    depthStencil = new VkClearDepthStencilValue(1.0f, 0)
                };
            VkRenderPassBeginInfo renderPassBeginInfo = new VkRenderPassBeginInfo
            {
                sType = VkStructureType.RenderPassBeginInfo,
                pNext = null,
                framebuffer = _framebuffers[imageIndex],
                renderArea = new Vortice.Mathematics.Rectangle(0, 0, Host.Width, Host.Height),
                clearValueCount = 2,
                pClearValues = clearValues,
                renderPass = _renderPass
            };
            vkCmdBeginRenderPass(cmdBuffer, &renderPassBeginInfo, VkSubpassContents.Inline);

            VkDescriptorSet descriptorSet = _descriptorSet;
            vkCmdBindDescriptorSets(cmdBuffer, VkPipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &descriptorSet);

            ulong* offsets = stackalloc ulong[1]
                {
                0
                };
            vkCmdBindPipeline(cmdBuffer, VkPipelineBindPoint.Graphics, _pipeline);
            VkBuffer buffer = _cubeVertices.Buffer;
            vkCmdBindVertexBuffers(cmdBuffer, 0, 1, &buffer, offsets);
            vkCmdBindIndexBuffer(cmdBuffer, _cubeIndices.Buffer, 0, VkIndexType.Uint32);
            vkCmdDrawIndexed(cmdBuffer, (uint)_cubeIndices.Count, 1, 0, 0, 0);
            vkCmdEndRenderPass(cmdBuffer);
        }

        private VkSampler CreateSampler()
        {
            var createInfo = new VkSamplerCreateInfo
            {
                sType = VkStructureType.SamplerCreateInfo,
                magFilter = VkFilter.Linear,
                minFilter = VkFilter.Linear,
                mipmapMode = VkSamplerMipmapMode.Linear
            };
            // We also enable anisotropic filtering. Because that feature is optional, it must be
            // checked if it is supported by the device.
            if (Context.Features.samplerAnisotropy)
            {
                createInfo.anisotropyEnable = true;
                createInfo.maxAnisotropy = Context.Properties.limits.maxSamplerAnisotropy;
            }
            else
            {
                createInfo.maxAnisotropy = 1.0f;
            }

            VkSampler sampler;
            vkCreateSampler(Context.Device, &createInfo, null, out sampler).CheckResult();
            return sampler;
        }

        private void SetViewProjection()
        {
            const float cameraDistance = 2.5f;
            _wvp.View = Matrix4x4.CreateLookAt(Vector3.UnitZ * cameraDistance, Vector3.Zero, Vector3.UnitY);
            _wvp.Projection = Matrix4x4.CreatePerspectiveFieldOfView(
                (float)Math.PI / 4,
                (float)Host.Width / Host.Height,
                1.0f, 1000.0f);
        }

        private void UpdateUniformBuffers()
        {
            int size = Unsafe.SizeOf<WorldViewProjection>();

            void* ppData;
            vkMapMemory(Context.Device, _uniformBuffer.Memory, 0, (ulong)size, 0, &ppData);

            void* srcPtr = Unsafe.AsPointer(ref _wvp);
            Unsafe.CopyBlock(ppData, srcPtr, (uint)size);

            vkUnmapMemory(Context.Device, _uniformBuffer.Memory);
        }

        private VkDescriptorPool CreateDescriptorPool()
        {
            VkDescriptorPoolSize* descriptorPoolSizePtr = stackalloc VkDescriptorPoolSize[2]
            {
                new VkDescriptorPoolSize { type = VkDescriptorType.UniformBuffer, descriptorCount = 1},
                new VkDescriptorPoolSize { type = VkDescriptorType.CombinedImageSampler, descriptorCount = 1 }
            };

            var createInfo = new VkDescriptorPoolCreateInfo()
            {
                sType = VkStructureType.DescriptorPoolCreateInfo,
                pNext = null,
                poolSizeCount = 2,
                pPoolSizes = descriptorPoolSizePtr,
                maxSets = 2
            };

            VkDescriptorPool descriptorPool;
            vkCreateDescriptorPool(Context.Device, &createInfo, null, out descriptorPool).CheckResult();

            return descriptorPool;
        }

        private VkDescriptorSet CreateDescriptorSet(VkDescriptorPool descriptorPool)
        {
            VkDescriptorSetLayout layout = _descriptorSetLayout;

            var allocInfo = new VkDescriptorSetAllocateInfo
            {
                sType = VkStructureType.DescriptorSetAllocateInfo,
                pNext = null,
                descriptorPool = descriptorPool,
                descriptorSetCount = 1,
                pSetLayouts = &layout
            };

            VkDescriptorSet descriptorSet;

            vkAllocateDescriptorSets(Context.Device, &allocInfo, &descriptorSet);

            VkBuffer uniformBuffer = _uniformBuffer.Buffer;
            // Update the descriptor set for the shader binding point.
            VkDescriptorBufferInfo bufferInfo1 = new VkDescriptorBufferInfo
            {
                buffer = _uniformBuffer.Buffer,
                offset = 0,
                range = ulong.MaxValue
            };

            VkDescriptorBufferInfo bufferInfo = new VkDescriptorBufferInfo
            {
                buffer = _uniformBuffer.Buffer,
                offset = 0,
                range = ulong.MaxValue
            };

            VkDescriptorImageInfo imageInfo = new VkDescriptorImageInfo
            {
                sampler = _sampler,
                imageView = _cubeTexture.View,
                imageLayout = VkImageLayout.ShaderReadOnlyOptimal
            };


            VkWriteDescriptorSet* writeDescriptorSetsPtr = stackalloc VkWriteDescriptorSet[2]
            {
                new VkWriteDescriptorSet
                {
                    sType = VkStructureType.WriteDescriptorSet,
                    pNext = null,
                    dstBinding = 0,
                    descriptorCount = 1,
                    descriptorType = VkDescriptorType.UniformBuffer,
                    pBufferInfo = &bufferInfo,
                    dstSet = descriptorSet
                },
                new VkWriteDescriptorSet
                {
                    sType = VkStructureType.WriteDescriptorSet,
                    pNext = null,
                    dstBinding = 1,
                    descriptorCount = 1,
                    descriptorType = VkDescriptorType.CombinedImageSampler,
                    pImageInfo = &imageInfo,
                    dstSet = descriptorSet,
                }
            };

            vkUpdateDescriptorSets(Context.Device, 2, writeDescriptorSetsPtr, 0, null);

            return descriptorSet;
        }

        private VkDescriptorSetLayout CreateDescriptorSetLayout()
        {
            VkDescriptorSetLayoutBinding* bindings = stackalloc VkDescriptorSetLayoutBinding[2];
            bindings[0] = new VkDescriptorSetLayoutBinding
            {
                binding = 0,
                descriptorType = VkDescriptorType.UniformBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Vertex
            };
            bindings[1] = new VkDescriptorSetLayoutBinding
            {
                binding = 1,
                descriptorType = VkDescriptorType.CombinedImageSampler,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Fragment
            };
            var createInfo = new VkDescriptorSetLayoutCreateInfo
            {
                sType = VkStructureType.DescriptorSetLayoutCreateInfo,
                pNext = null,
                bindingCount = 2,
                pBindings = bindings
            };
            VkDescriptorSetLayout layout;
            vkCreateDescriptorSetLayout(Context.Device, &createInfo, null, out layout).CheckResult();
            return layout;
        }

        private VkPipelineLayout CreatePipelineLayout()
        {
            VkDescriptorSetLayout layout = _descriptorSetLayout;
            var createInfo = new VkPipelineLayoutCreateInfo
            {
                sType = VkStructureType.PipelineLayoutCreateInfo,
                pNext = null,
                setLayoutCount = 1,
                pSetLayouts = &layout
            };

            VkPipelineLayout pipelineLayout;
            vkCreatePipelineLayout(Context.Device, &createInfo, null, out pipelineLayout).CheckResult();
            return pipelineLayout;
        }

        private VkRenderPass CreateRenderPass()
        {
            VkAttachmentDescription* attachments = stackalloc VkAttachmentDescription[2]
            {
                // Color attachment.
                new VkAttachmentDescription
                {
                    format = SwapchainFormat,
                    samples = VkSampleCountFlags.Count1,
                    loadOp = VkAttachmentLoadOp.Clear,
                    storeOp = VkAttachmentStoreOp.Store,
                    stencilLoadOp = VkAttachmentLoadOp.DontCare,
                    stencilStoreOp = VkAttachmentStoreOp.DontCare,
                    initialLayout = VkImageLayout.Undefined,
                    finalLayout = VkImageLayout.PresentSrcKHR
                },
                // Depth attachment.
                new VkAttachmentDescription
                {
                    format = _depthStencilBuffer.Format,
                    samples = VkSampleCountFlags.Count1,
                    loadOp = VkAttachmentLoadOp.Clear,
                    storeOp = VkAttachmentStoreOp.DontCare,
                    stencilLoadOp = VkAttachmentLoadOp.DontCare,
                    stencilStoreOp = VkAttachmentStoreOp.DontCare,
                    initialLayout = VkImageLayout.Undefined,
                    finalLayout = VkImageLayout.DepthStencilAttachmentOptimal
                }
            };


            VkAttachmentReference colorAttachment = new VkAttachmentReference
            {
                attachment = 0,
                layout = VkImageLayout.ColorAttachmentOptimal
            };
            VkAttachmentReference depthStencilAttachment = new VkAttachmentReference
            {
                attachment = 1,
                layout = VkImageLayout.DepthStencilAttachmentOptimal
            };

            var subpass = new VkSubpassDescription
            {
                colorAttachmentCount = 1,
                pColorAttachments = &colorAttachment,
                pDepthStencilAttachment = &depthStencilAttachment
            };

            VkSubpassDependency* dependencies = stackalloc VkSubpassDependency[2]
            {
                new VkSubpassDependency
                {
                    srcSubpass = uint.MaxValue, // SubpassExternal ?
                    dstSubpass = 0,
                    srcStageMask = VkPipelineStageFlags.BottomOfPipe,
                    dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
                    srcAccessMask = VkAccessFlags.MemoryRead,
                    dstAccessMask = VkAccessFlags.ColorAttachmentRead | VkAccessFlags.ColorAttachmentWrite,
                    dependencyFlags = VkDependencyFlags.ByRegion
                },
                new VkSubpassDependency
                {
                    srcSubpass = 0,
                    dstSubpass = uint.MaxValue, // SubpassExternal ?
                    srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
                    dstStageMask = VkPipelineStageFlags.BottomOfPipe,
                    srcAccessMask = VkAccessFlags.ColorAttachmentRead | VkAccessFlags.ColorAttachmentWrite,
                    dstAccessMask = VkAccessFlags.MemoryRead,
                    dependencyFlags = VkDependencyFlags.ByRegion
                }
            };

            var createInfo = new VkRenderPassCreateInfo
            {
                sType = VkStructureType.RenderPassCreateInfo,
                pNext = null,
                subpassCount = 1,
                pSubpasses = &subpass,
                attachmentCount = 2,
                pAttachments = attachments,
                dependencyCount = 2,
                pDependencies = dependencies
            };


            VkRenderPass renderPass;
            vkCreateRenderPass(Context.Device, &createInfo, null, out renderPass).CheckResult();
            return renderPass;
        }

        private VkImageView[] CreateImageViews()
        {
            var imageViews = new VkImageView[SwapchainImages.Length];
            for (int i = 0; i < SwapchainImages.Length; i++)
            {
                VkImageSubresourceRange range = new VkImageSubresourceRange
                {
                    aspectMask = VkImageAspectFlags.Color,
                    baseMipLevel = 0,
                    levelCount = 1,
                    baseArrayLayer = 0,
                    layerCount = 1
                };

                VkImageViewCreateInfo createInfo = new VkImageViewCreateInfo
                {
                    sType = VkStructureType.ImageViewCreateInfo,
                    pNext = null,
                    format = SwapchainFormat,
                    viewType = VkImageViewType.Image2D,
                    subresourceRange = range,
                    image = SwapchainImages[i]
                };

                VkImageView imageView;
                vkCreateImageView(Context.Device, &createInfo, null, out imageView);

                imageViews[i] = imageView;
            }
            return imageViews;
        }

        private VkFramebuffer[] CreateFramebuffers()
        {
            var framebuffers = new VkFramebuffer[SwapchainImages.Length];

            for (int i = 0; i < SwapchainImages.Length; i++)
            {
                VkImageView* attachments = stackalloc VkImageView[2]
                {
                    _imageViews[i],
                    _depthStencilBuffer.View
                };

                var createInfo = new VkFramebufferCreateInfo
                {
                    sType = VkStructureType.FramebufferCreateInfo,
                    pNext = null,
                    attachmentCount = 2,
                    pAttachments = attachments,
                    width = (uint)Host.Width,
                    height = (uint)Host.Height,
                    renderPass = _renderPass,
                    layers = 1
                };

                VkFramebuffer frameBuffer;
                vkCreateFramebuffer(Context.Device, &createInfo, null, out frameBuffer).CheckResult();

                framebuffers[i] = frameBuffer;
            }
            return framebuffers;
        }

        private VkPipeline CreateGraphicsPipeline()
        {
            // Create shader modules. Shader modules are one of the objects required to create the
            // graphics pipeline. But after the pipeline is created, we don't need these shader
            // modules anymore, so we dispose them.
            VkShaderModule vertexShader   = Content.LoadShader("Shader.vert.spv");
            VkShaderModule fragmentShader = Content.LoadShader("Shader.frag.spv");
            VkPipelineShaderStageCreateInfo* shaderStageCreateInfos = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                new VkPipelineShaderStageCreateInfo
                {
                     sType = VkStructureType.PipelineShaderStageCreateInfo,
                     pNext = null,
                     stage = VkShaderStageFlags.Vertex,
                     module = vertexShader,
                     pName = Interop.String.ToPointer("main")
                },
                new VkPipelineShaderStageCreateInfo
                {
                    sType = VkStructureType.PipelineShaderStageCreateInfo,
                    pNext = null,
                    stage = VkShaderStageFlags.Fragment,
                    module = fragmentShader,
                    pName = Interop.String.ToPointer("main")
                }
            };

            VkVertexInputBindingDescription vertexInputBindingDescription = new VkVertexInputBindingDescription
            {
                binding = 0,
                stride = (uint)Unsafe.SizeOf<Vertex>(),
                inputRate = VkVertexInputRate.Vertex
            };
            VkVertexInputAttributeDescription* vertexInputAttributeDescription = stackalloc VkVertexInputAttributeDescription[3]
            {
                new VkVertexInputAttributeDescription
                {
                    location = 0,
                    binding = 0,
                    format = VkFormat.R32G32B32A32SFloat,
                    offset = 0
                },  // Position.
                new VkVertexInputAttributeDescription
                {
                    location = 1,
                    binding = 0,
                    format = VkFormat.R32G32B32SFloat,
                    offset = 12
                }, // Normal.
                new VkVertexInputAttributeDescription
                {
                    location = 2,
                    binding = 0,
                    format = VkFormat.R32G32SFloat,
                    offset = 24
                }// TexCoord.
            };
            var vertexInputStateCreateInfo = new VkPipelineVertexInputStateCreateInfo
            {
                sType = VkStructureType.PipelineVertexInputStateCreateInfo,
                pNext = null,
                vertexBindingDescriptionCount = 1,
                pVertexBindingDescriptions = &vertexInputBindingDescription,
                vertexAttributeDescriptionCount = 3,
                pVertexAttributeDescriptions = vertexInputAttributeDescription
            };
            var inputAssemblyStateCreateInfo = new VkPipelineInputAssemblyStateCreateInfo
            {
                sType = VkStructureType.PipelineInputAssemblyStateCreateInfo,
                pNext = null,
                topology = VkPrimitiveTopology.TriangleList
            };

            Viewport viewport = new Viewport(0, 0, Host.Width, Host.Height);
            Rectangle scissor = new Rectangle(0, 0, Host.Width, Host.Height);

            var viewportStateCreateInfo = new VkPipelineViewportStateCreateInfo
            {
                sType = VkStructureType.PipelineViewportStateCreateInfo,
                pNext = null,
                viewportCount = 1,
                pViewports = &viewport,
                scissorCount = 1,
                pScissors = &scissor
            };
            var rasterizationStateCreateInfo = new VkPipelineRasterizationStateCreateInfo
            {
                sType = VkStructureType.PipelineRasterizationStateCreateInfo,
                polygonMode = VkPolygonMode.Fill,
                cullMode = VkCullModeFlags.Back,
                frontFace = VkFrontFace.CounterClockwise,
                lineWidth = 1.0f
            };
            var multisampleStateCreateInfo = new VkPipelineMultisampleStateCreateInfo
            {
                sType = VkStructureType.PipelineMultisampleStateCreateInfo,
                rasterizationSamples = VkSampleCountFlags.Count1,
                minSampleShading = 1.0f
            };
            var depthStencilStateCreateInfo = new VkPipelineDepthStencilStateCreateInfo
            {
                sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
                depthTestEnable = true,
                depthWriteEnable = true,
                depthCompareOp = VkCompareOp.LessOrEqual,
                back = new VkStencilOpState
                {
                    failOp = VkStencilOp.Keep,
                    passOp = VkStencilOp.Keep,
                    compareOp = VkCompareOp.Always
                },
                front = new VkStencilOpState
                {
                    failOp = VkStencilOp.Keep,
                    passOp = VkStencilOp.Keep,
                    compareOp = VkCompareOp.Always
                }
            };
            var colorBlendAttachmentState = new VkPipelineColorBlendAttachmentState
            {
                srcColorBlendFactor = VkBlendFactor.One,
                dstColorBlendFactor = VkBlendFactor.Zero,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.One,
                dstAlphaBlendFactor = VkBlendFactor.Zero,
                alphaBlendOp = VkBlendOp.Add,
                colorWriteMask = VkColorComponentFlags.All
            };
            var colorBlendStateCreateInfo = new VkPipelineColorBlendStateCreateInfo
            {
                sType = VkStructureType.PipelineColorBlendStateCreateInfo,
                pNext = null,
                attachmentCount = 1,
                pAttachments = & colorBlendAttachmentState
            };

            var pipelineCreateInfo = new VkGraphicsPipelineCreateInfo
            {
                sType = VkStructureType.GraphicsPipelineCreateInfo,
                pNext = null,
                layout = _pipelineLayout,
                renderPass = _renderPass,
                subpass = (uint)0,
                stageCount = 2,
                pStages = shaderStageCreateInfos,
                pInputAssemblyState = &inputAssemblyStateCreateInfo,
                pVertexInputState = &vertexInputStateCreateInfo,
                pRasterizationState = &rasterizationStateCreateInfo,
                pMultisampleState = &multisampleStateCreateInfo,
                pColorBlendState = &colorBlendStateCreateInfo,
                pDepthStencilState = &depthStencilStateCreateInfo,
                pViewportState = &viewportStateCreateInfo
            };


            VkPipeline pipeline;
            VkResult result = vkCreateGraphicsPipelines(Context.Device, VkPipelineCache.Null, 1, &pipelineCreateInfo, null, &pipeline);
            result.CheckResult();

            return pipeline;
        }
    }
}
