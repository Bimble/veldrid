﻿using System;
using Vulkan;
using static Vulkan.VulkanNative;
using static Vd2.Vk.VulkanUtil;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Diagnostics;

namespace Vd2.Vk
{
    internal unsafe class VkCommandList : CommandList
    {
        private readonly VkGraphicsDevice _gd;
        private VkCommandBuffer _cb;
        private VkFramebufferBase _currentFramebuffer;
        private VkRenderPass _activeRenderPass;
        private VkPipeline _currentPipeline;

        private List<VkImage> _imagesToDestroy;
        private List<VkMemoryBlock> _memoriesToFree;

        private bool _commandBufferBegun;
        private bool _commandBufferEnded;
        private VkResourceSet _currentResourceSet;

        public VkCommandBuffer CommandBuffer => _cb;

        public VkCommandList(VkGraphicsDevice gd, ref CommandListDescription description)
            : base(ref description)
        {
            _gd = gd;
            VkCommandBufferAllocateInfo cbAI = VkCommandBufferAllocateInfo.New();
            cbAI.commandPool = gd.GraphicsCommandPool;
            cbAI.commandBufferCount = 1;
            cbAI.level = VkCommandBufferLevel.Primary;
            VkResult result = vkAllocateCommandBuffers(gd.Device, ref cbAI, out _cb);
            CheckResult(result);
        }

        public override void Begin()
        {
            if (_commandBufferBegun)
            {
                throw new VdException(
                    "CommandList must be in it's initial state, or End() must have been called, for Begin() to be valid to call.");
            }
            if (_commandBufferEnded)
            {
                _commandBufferEnded = false;
                vkResetCommandBuffer(_cb, VkCommandBufferResetFlags.None);
            }

            VkCommandBufferBeginInfo beginInfo = VkCommandBufferBeginInfo.New();
            vkBeginCommandBuffer(_cb, ref beginInfo);
            _commandBufferBegun = true;
        }

        public override void ClearColorTarget(uint index, RgbaFloat clearColor)
        {
            VkClearValue clearValue = new VkClearValue { color = new VkClearColorValue(clearColor.R, clearColor.G, clearColor.B, clearColor.A) };
            VkClearAttachment clearAttachment = new VkClearAttachment
            {
                colorAttachment = index,
                aspectMask = VkImageAspectFlags.Color,
                clearValue = clearValue
            };

            Texture2D colorTex = ((Texture2D)_currentFramebuffer.ColorTextures[(int)index]);
            VkClearRect clearRect = new VkClearRect
            {
                baseArrayLayer = 0,
                layerCount = 1,
                rect = new VkRect2D(0, 0, colorTex.Width, colorTex.Height)
            };

            vkCmdClearAttachments(_cb, 1, ref clearAttachment, 1, ref clearRect);
        }

        public override void ClearDepthTarget(float depth)
        {
            VkClearValue clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(depth, 0) };
            VkClearAttachment clearAttachment = new VkClearAttachment
            {
                aspectMask = VkImageAspectFlags.Depth,
                clearValue = clearValue
            };

            Texture2D depthTex = (Texture2D)_currentFramebuffer.DepthTexture;
            VkClearRect clearRect = new VkClearRect
            {
                baseArrayLayer = 0,
                layerCount = 1,
                rect = new VkRect2D(0, 0, depthTex.Width, depthTex.Height)
            };

            vkCmdClearAttachments(_cb, 1, ref clearAttachment, 1, ref clearRect);
        }

        public override void Draw(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            VkDescriptorSet ds = _currentResourceSet.DescriptorSet;
            vkCmdBindDescriptorSets(
                _cb,
                 VkPipelineBindPoint.Graphics,
                 _currentPipeline.PipelineLayout,
                 0,
                 1,
                 ref ds,
                 0,
                 null);

            vkCmdDrawIndexed(_cb, indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        }

        public override void End()
        {
            if (!_commandBufferBegun)
            {
                throw new VdException("CommandBuffer must have been started before End() may be called.");
            }

            _commandBufferBegun = false;
            _commandBufferEnded = true;

            if (_activeRenderPass != VkRenderPass.Null)
            {
                EndCurrentRenderPass();
            }

            vkEndCommandBuffer(_cb);

            //foreach (VkImage image in _imagesToDestroy)
            //{
            //    vkDestroyImage(_gd.Device, image, null);
            //}
            //foreach (VkMemoryBlock memory in _memoriesToFree)
            //{
            //    _gd.MemoryManager.Free(memory);
            //}
        }

        public override void SetFramebuffer(Framebuffer fb)
        {
            if (_activeRenderPass.Handle != IntPtr.Zero)
            {
                EndCurrentRenderPass();
            }

            VkFramebufferBase vkFB = Util.AssertSubtype<Framebuffer, VkFramebufferBase>(fb);
            _currentFramebuffer = vkFB;

            BeginCurrentRenderPass();
        }

        private void BeginCurrentRenderPass()
        {
            Debug.Assert(_activeRenderPass == VkRenderPass.Null);

            VkRenderPassBeginInfo renderPassBI = VkRenderPassBeginInfo.New();
            renderPassBI.framebuffer = _currentFramebuffer.CurrentFramebuffer;
            renderPassBI.renderPass = _currentFramebuffer.RenderPass;
            renderPassBI.renderArea = new VkRect2D(_currentFramebuffer.Width, _currentFramebuffer.Height);
            vkCmdBeginRenderPass(_cb, ref renderPassBI, VkSubpassContents.Inline);
            _activeRenderPass = _currentFramebuffer.RenderPass;
        }

        private void EndCurrentRenderPass()
        {
            Debug.Assert(_activeRenderPass != VkRenderPass.Null);
            vkCmdEndRenderPass(_cb);
            _activeRenderPass = VkRenderPass.Null;
        }

        public override void SetVertexBuffer(uint index, VertexBuffer vb)
        {
            VkBuffer vkBuffer = Util.AssertSubtype<VertexBuffer, VkVertexBuffer>(vb);
            Vulkan.VkBuffer deviceBuffer = vkBuffer.DeviceBuffer;
            ulong offset = 0;
            vkCmdBindVertexBuffers(_cb, index, 1, ref deviceBuffer, ref offset);
        }

        public override void SetIndexBuffer(IndexBuffer ib)
        {
            VkBuffer vkBuffer = Util.AssertSubtype<IndexBuffer, VkIndexBuffer>(ib);
            vkCmdBindIndexBuffer(_cb, vkBuffer.DeviceBuffer, 0, VkFormats.VdToVkIndexFormat(ib.Format));
        }

        public override void SetPipeline(Pipeline pipeline)
        {
            VkPipeline vkPipeline = Util.AssertSubtype<Pipeline, VkPipeline>(pipeline);
            vkCmdBindPipeline(_cb, VkPipelineBindPoint.Graphics, vkPipeline.DevicePipeline);
            _currentPipeline = vkPipeline;
        }

        public override void SetResourceSet(ResourceSet rs)
        {
            VkResourceSet vkRS = Util.AssertSubtype<ResourceSet, VkResourceSet>(rs);

            _currentResourceSet = vkRS;
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            VkRect2D scissor = new VkRect2D((int)x, (int)y, (int)width, (int)height);
            vkCmdSetScissor(_cb, index, 1, ref scissor);
        }

        public override void SetViewport(uint index, ref Viewport viewport)
        {
            VkViewport vkViewport = new VkViewport
            {
                x = viewport.X,
                y = viewport.Y,
                width = viewport.Width,
                height = viewport.Height,
                minDepth = viewport.MinDepth,
                maxDepth = viewport.MaxDepth
            };

            vkCmdSetViewport(_cb, index, 1, ref vkViewport);
        }

        public override void UpdateBuffer(Buffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            VkBuffer vkBuffer = Util.AssertSubtype<Buffer, VkBuffer>(buffer);
            IntPtr mappedPtr = MapBuffer(vkBuffer, sizeInBytes);
            byte* destPtr = (byte*)mappedPtr + bufferOffsetInBytes;
            Unsafe.CopyBlock(destPtr, source.ToPointer(), sizeInBytes);
            UnmapBuffer(vkBuffer);
        }

        private IntPtr MapBuffer(VkBuffer buffer, uint numBytes)
        {
            void* mappedPtr;
            VkResult result = vkMapMemory(_gd.Device, buffer.Memory.DeviceMemory, buffer.Memory.Offset, numBytes, 0, &mappedPtr);
            CheckResult(result);
            return (IntPtr)mappedPtr;
        }

        private void UnmapBuffer(VkBuffer buffer)
        {
            vkUnmapMemory(_gd.Device, buffer.Memory.DeviceMemory);
        }


        public override void UpdateTexture2D(
            Texture2D texture2D,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint width,
            uint height,
            uint mipLevel,
            uint arrayLayer)
        {
            VkTexture2D vkTex2D = Util.AssertSubtype<Texture2D, VkTexture2D>(texture2D);

            if (x != 0 || y != 0)
            {
                throw new NotImplementedException();
            }

            // First, create a staging texture.
            CreateImage(
                _gd.Device,
                _gd.PhysicalDeviceMemProperties,
                _gd.MemoryManager,
                width,
                height,
                1,
                VkFormats.VdToVkPixelFormat(vkTex2D.Format),
                VkImageTiling.Linear,
                VkImageUsageFlags.TransferSrc,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent,
                out VkImage stagingImage,
                out VkMemoryBlock stagingMemory);

            VkImageSubresource subresource = new VkImageSubresource();
            subresource.aspectMask = VkImageAspectFlags.Color;
            subresource.mipLevel = 0;
            subresource.arrayLayer = 0;
            vkGetImageSubresourceLayout(_gd.Device, stagingImage, ref subresource, out VkSubresourceLayout stagingLayout);
            ulong rowPitch = stagingLayout.rowPitch;

            void* mappedPtr;
            VkResult result = vkMapMemory(_gd.Device, stagingMemory.DeviceMemory, stagingMemory.Offset, stagingLayout.size, 0, &mappedPtr);
            CheckResult(result);

            if (rowPitch == width)
            {
                System.Buffer.MemoryCopy(source.ToPointer(), mappedPtr, sizeInBytes, sizeInBytes);
            }
            else
            {
                uint pixelSizeInBytes = FormatHelpers.GetSizeInBytes(texture2D.Format);
                for (uint yy = 0; yy < height; yy++)
                {
                    byte* dstRowStart = ((byte*)mappedPtr) + (rowPitch * yy);
                    byte* srcRowStart = ((byte*)source.ToPointer()) + (width * yy * pixelSizeInBytes);
                    Unsafe.CopyBlock(dstRowStart, srcRowStart, width * pixelSizeInBytes);
                }
            }

            vkUnmapMemory(_gd.Device, stagingMemory.DeviceMemory);

            TransitionImageLayout(stagingImage, 0, 1, 0, 1, VkImageLayout.Preinitialized, VkImageLayout.TransferSrcOptimal);
            TransitionImageLayout(vkTex2D.DeviceImage, mipLevel, 1, 0, 1, vkTex2D.ImageLayouts[mipLevel], VkImageLayout.TransferDstOptimal);
            CopyImage(stagingImage, 0, vkTex2D.DeviceImage, mipLevel, width, height);
            TransitionImageLayout(vkTex2D.DeviceImage, mipLevel, 1, 0, 1, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);
            vkTex2D.ImageLayouts[mipLevel] = VkImageLayout.ShaderReadOnlyOptimal;

            if (_imagesToDestroy == null)
            {
                _imagesToDestroy = new List<VkImage>();
            }
            _imagesToDestroy.Add(stagingImage);

            if (_memoriesToFree == null)
            {
                _memoriesToFree = new List<VkMemoryBlock>();
            }
            _memoriesToFree.Add(stagingMemory);
        }

        public override void UpdateTextureCube(
            TextureCube textureCube,
            IntPtr source,
            uint sizeInBytes,
            CubeFace face,
            uint x,
            uint y,
            uint width,
            uint height,
            uint mipLevel,
            uint arrayLayer)
        {
            VkTextureCube vkTexCube = Util.AssertSubtype<TextureCube, VkTextureCube>(textureCube);

            if (x != 0 || y != 0)
            {
                throw new NotImplementedException();
            }

            // First, create a staging texture.
            CreateImage(
                _gd.Device,
                _gd.PhysicalDeviceMemProperties,
                _gd.MemoryManager,
                width,
                height,
                1,
                VkFormats.VdToVkPixelFormat(vkTexCube.Format),
                VkImageTiling.Linear,
                VkImageUsageFlags.TransferSrc,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent,
                out VkImage stagingImage,
                out VkMemoryBlock stagingMemory);

            VkImageSubresource subresource = new VkImageSubresource();
            subresource.aspectMask = VkImageAspectFlags.Color;
            subresource.mipLevel = 0;
            subresource.arrayLayer = 0;
            vkGetImageSubresourceLayout(_gd.Device, stagingImage, ref subresource, out VkSubresourceLayout stagingLayout);
            ulong rowPitch = stagingLayout.rowPitch;

            void* mappedPtr;
            VkResult result = vkMapMemory(_gd.Device, stagingMemory.DeviceMemory, stagingMemory.Offset, stagingLayout.size, 0, &mappedPtr);
            CheckResult(result);

            if (rowPitch == width)
            {
                System.Buffer.MemoryCopy(source.ToPointer(), mappedPtr, sizeInBytes, sizeInBytes);
            }
            else
            {
                uint pixelSizeInBytes = FormatHelpers.GetSizeInBytes(vkTexCube.Format);
                for (uint yy = 0; yy < height; yy++)
                {
                    byte* dstRowStart = ((byte*)mappedPtr) + (rowPitch * yy);
                    byte* srcRowStart = ((byte*)source.ToPointer()) + (width * yy * pixelSizeInBytes);
                    Unsafe.CopyBlock(dstRowStart, srcRowStart, width * pixelSizeInBytes);
                }
            }

            vkUnmapMemory(_gd.Device, stagingMemory.DeviceMemory);

            uint cubeArrayLayer = GetArrayLayer(face);

            TransitionImageLayout(stagingImage, 0, 1, 0, 1, VkImageLayout.Preinitialized, VkImageLayout.TransferSrcOptimal);
            TransitionImageLayout(vkTexCube.DeviceImage, 0, 1, 0, 6, vkTexCube.ImageLayout, VkImageLayout.TransferDstOptimal);
            CopyImage(stagingImage, 0, vkTexCube.DeviceImage, mipLevel, width, height, cubeArrayLayer);
            TransitionImageLayout(vkTexCube.DeviceImage, 0, 1, 0, 6, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);
            vkTexCube.ImageLayout = VkImageLayout.ShaderReadOnlyOptimal;

            if (_imagesToDestroy == null)
            {
                _imagesToDestroy = new List<VkImage>();
            }
            _imagesToDestroy.Add(stagingImage);

            if (_memoriesToFree == null)
            {
                _memoriesToFree = new List<VkMemoryBlock>();
            }
            _memoriesToFree.Add(stagingMemory);
        }

        private uint GetArrayLayer(CubeFace face)
        {
            switch (face)
            {
                case CubeFace.NegativeX:
                    return 1;
                case CubeFace.PositiveX:
                    return 0;
                case CubeFace.NegativeY:
                    return 3;
                case CubeFace.PositiveY:
                    return 2;
                case CubeFace.NegativeZ:
                    return 4;
                case CubeFace.PositiveZ:
                    return 5;
                default:
                    throw Illegal.Value<CubeFace>();
            }
        }

        protected void TransitionImageLayout(
            VkImage image,
            uint baseMipLevel,
            uint levelCount,
            uint baseArrayLayer,
            uint layerCount,
            VkImageLayout oldLayout,
            VkImageLayout newLayout)
        {
            Debug.Assert(oldLayout != newLayout);
            VkImageMemoryBarrier barrier = VkImageMemoryBarrier.New();
            barrier.oldLayout = oldLayout;
            barrier.newLayout = newLayout;
            barrier.srcQueueFamilyIndex = QueueFamilyIgnored;
            barrier.dstQueueFamilyIndex = QueueFamilyIgnored;
            barrier.image = image;
            barrier.subresourceRange.aspectMask = VkImageAspectFlags.Color;
            barrier.subresourceRange.baseMipLevel = baseMipLevel;
            barrier.subresourceRange.levelCount = levelCount;
            barrier.subresourceRange.baseArrayLayer = baseArrayLayer;
            barrier.subresourceRange.layerCount = layerCount;
            if (oldLayout == VkImageLayout.TransferSrcOptimal)
            {
                barrier.srcAccessMask = VkAccessFlags.TransferRead;
            }
            else if (oldLayout == VkImageLayout.TransferDstOptimal)
            {
                barrier.srcAccessMask = VkAccessFlags.TransferWrite;
            }
            else if (oldLayout == VkImageLayout.ShaderReadOnlyOptimal)
            {
                barrier.srcAccessMask = VkAccessFlags.ShaderRead;
            }

            if (newLayout == VkImageLayout.TransferSrcOptimal)
            {
                barrier.dstAccessMask = VkAccessFlags.TransferRead;
            }
            else if (newLayout == VkImageLayout.TransferDstOptimal)
            {
                barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            }
            else if (newLayout == VkImageLayout.ShaderReadOnlyOptimal)
            {
                barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            }

            vkCmdPipelineBarrier(
                //cbTemp,
                _cb,
                VkPipelineStageFlags.TopOfPipe,
                VkPipelineStageFlags.TopOfPipe,
                VkDependencyFlags.None,
                0, null,
                0, null,
                1, &barrier);
        }

        protected void CopyImage(
            VkImage srcImage,
            uint srcMipLevel,
            VkImage dstImage,
            uint dstMipLevel,
            uint width,
            uint height,
            uint baseArrayLayer = 0)
        {
            VkImageSubresourceLayers srcSubresource = new VkImageSubresourceLayers();
            srcSubresource.mipLevel = srcMipLevel;
            srcSubresource.layerCount = 1;
            srcSubresource.aspectMask = VkImageAspectFlags.Color;
            srcSubresource.baseArrayLayer = 0;

            VkImageSubresourceLayers dstSubresource = new VkImageSubresourceLayers();
            dstSubresource.mipLevel = dstMipLevel;
            dstSubresource.baseArrayLayer = baseArrayLayer;
            dstSubresource.layerCount = 1;
            dstSubresource.aspectMask = VkImageAspectFlags.Color;

            VkImageCopy region = new VkImageCopy();
            region.dstSubresource = dstSubresource;
            region.srcSubresource = srcSubresource;
            region.extent.width = width;
            region.extent.height = height;
            region.extent.depth = 1;

            vkCmdCopyImage(
                //tempCB,
                _cb,
                srcImage,
                VkImageLayout.TransferSrcOptimal,
                dstImage,
                VkImageLayout.TransferDstOptimal,
                1,
                ref region);
        }

        public override void Dispose()
        {
            vkFreeCommandBuffers(_gd.Device, _gd.GraphicsCommandPool, 1, ref _cb);
        }
    }
}