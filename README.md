# TexturedCube-Vortice
Vulkan Textured Cube example using Vortice

## What is this?
This takes the Textured Cube sample from https://github.com/discosultan/VulkanCore and translates it to work with https://github.com/amerkoleci/Vortice.Vulkan

### Why?
I've been developing a Vulkan renderer for a while, and it was based on VulkanCore. VulkanCore provides a nice thin OO wrapper around Vulkan, but as names are changed and functionality is moved onto various classes, it does mean that it's not trivial to translate sample code written against the C-api.

https://github.com/discosultan/VulkanCore/issues/40 indicates that the project may no longer be under development.

Instead of translating my renderer over in one large change, I first ported over the TexturedCube sample, and thought I'd make it available for other devs.
