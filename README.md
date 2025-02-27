# Carbonara
Unity components for NOODLES Server (Alpha)

This repository contains code to help replicate Unity scenes over the NOODLES protocol. It is designed to sync game objects, transforms, and basic materials between Unity instances and other clients, such as headsets or browsers, using NOODLES over web sockets.

## Prerequisites

- Unity Version: 2023 and above
- NuGet Plugin for Unity: You need the NuGetForUnity plugin to manage dependencies.
- PeterO.CBOR Library: The code uses the PeterO.CBOR library for encoding/decoding CBOR data (via NuGet).

## Installation

- Download or clone this repository.
- Copy the NOODLES folder from the Assets directory into your own Unity project.
- Alternatively, you can add the NOODLES folder as a git submodule if preferred.

## Setting Up the NOODLES Server

To set up the NOODLES server in your project, follow these steps:

- Drag and drop the `NOOServer.cs` script onto an existing game object in your scene using the Unity Editor.
- Configure the NOOServer:
    - Hostname: Set the hostname or IP address to bind the server (e.g., "192.168.1.4").
    - Port: Set the port to listen for incoming clients (default is 50000).
- When you run the scene, the server will automatically start up

## Client Setup

Clients can connect via web sockets to the server by using the provided hostname (or IP address) and port. The protocol supports clients written in various languages. For client code examples, check out the InsightCenterNoodles organization.

## Supported Platforms

- Windows
- Mac
- Linux

Note: Mobile platforms have not yet been tested.

## Features

The NOODLES protocol replicates scenes recursively for all child objects of the server node. The following are currently supported:

- Transforms: Position, rotation, and scale
- Meshes: Basic mesh replication
- Materials: Standard PBR (Physically Based Rendering) shader supported
- Visibility: Controlled by standard visibility controls
- Replication: Controlled by the NOOVisibility script, which can be attached to any object for replication management

## Limitations
- Animations: Not supported
- Sounds: Not supported
- Terrain: Not supported
- Advanced Shaders: Only built-in PBR shaders are supported. Custom or advanced shaders may not function as expected.

## Performance Considerations
- Motion Sampling: The sample rate for motion replication is set to approximately 20 Hz to avoid overwhelming clients. You can adjust this depending on performance needs.
- Materials: Be cautious with materials, as advanced shaders outside of PBR may not work.
- Large meshes may take time to process

## License

This project is licensed under the MIT License.