// Licensed to the Gordon under one or more agreements.
// Gordon licenses this file to you under the MIT license.

using BenchmarkDotNet.Running;
using Vertex.Messaging.Bench;

BenchmarkSwitcher.FromAssembly(typeof(MessagingChannelBenchmarks).Assembly).Run(args);
