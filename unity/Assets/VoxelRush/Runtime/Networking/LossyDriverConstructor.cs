#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Collections;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using VoxelRush.Core.Hosting;

namespace VoxelRush.Networking
{
    /// <summary>
    /// Development builds only: builds Unity Transport's driver with its simulator stage appended to every pipeline,
    /// so packets are really dropped/delayed on send and receive and NGO's reliable channels must resend (VR-072).
    /// </summary>
    internal sealed class LossyDriverConstructor : INetworkStreamDriverConstructor
    {
        private readonly TransportSimulationOptions _options;
        private readonly uint _seed;

        public LossyDriverConstructor(TransportSimulationOptions options, uint seed)
        {
            _options = options;
            _seed = seed == 0 ? 1u : seed;
        }

        public void CreateDriver(UnityTransport transport, out NetworkDriver driver, out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline, out NetworkPipeline reliableSequencedPipeline)
        {
            NetworkSettings settings = transport.GetDefaultNetworkSettings();
            settings.WithSimulatorStageParameters(maxPacketCount: 1000, mode: ApplyMode.AllPackets,
                packetDelayMs: _options.DelayMs, packetDropPercentage: _options.LossPercent, randomSeed: _seed);
            driver = NetworkDriver.Create(new UDPNetworkInterface(), settings);
            transport.GetDefaultPipelineConfigurations(ref driver, out var unreliableFragmented, out var unreliableSequencedFragmented, out var reliableSequenced);
            unreliableFragmentedPipeline = driver.CreatePipeline(WithSimulator(unreliableFragmented));
            unreliableSequencedFragmentedPipeline = driver.CreatePipeline(WithSimulator(unreliableSequencedFragmented));
            reliableSequencedPipeline = driver.CreatePipeline(WithSimulator(reliableSequenced));
        }

        // The simulator goes last, nearest the wire (as NGO places it with the Multiplayer Tools simulator).
        private static NativeArray<NetworkPipelineStageId> WithSimulator(NativeArray<NetworkPipelineStageId> stages)
        {
            var result = new NativeArray<NetworkPipelineStageId>(stages.Length + 1, Allocator.Temp);
            NativeArray<NetworkPipelineStageId>.Copy(stages, result, stages.Length);
            result[stages.Length] = NetworkPipelineStageId.Get<SimulatorPipelineStage>();
            return result;
        }
    }
}
#endif
