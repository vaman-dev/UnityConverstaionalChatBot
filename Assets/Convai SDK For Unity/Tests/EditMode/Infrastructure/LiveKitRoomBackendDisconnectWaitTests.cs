using System.Threading;
using System.Threading.Tasks;
using Convai.Infrastructure.Networking.Connection;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class LiveKitRoomBackendDisconnectWaitTests
    {
        [Test]
        public async Task WaitForDisconnectCompletionAsync_WhenSignalWins_ReturnsCompleted()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();

                LiveKitRoomBackend.DisconnectWaitOutcome outcome =
                    await LiveKitRoomBackend.WaitForDisconnectCompletionAsync(
                        Task.CompletedTask,
                        1000,
                        cancellation.Token);

                Assert.AreEqual(LiveKitRoomBackend.DisconnectWaitOutcome.Completed, outcome);
            }
        }

        [Test]
        public async Task WaitForDisconnectCompletionAsync_WhenCallerCancels_ReturnsCancelled()
        {
            var disconnected = new TaskCompletionSource<bool>();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();

                LiveKitRoomBackend.DisconnectWaitOutcome outcome =
                    await LiveKitRoomBackend.WaitForDisconnectCompletionAsync(
                        disconnected.Task,
                        1000,
                        cancellation.Token);

                Assert.AreEqual(LiveKitRoomBackend.DisconnectWaitOutcome.Cancelled, outcome);
            }
        }

        [Test]
        public async Task WaitForDisconnectCompletionAsync_WhenDeadlineWins_ReturnsTimedOut()
        {
            var disconnected = new TaskCompletionSource<bool>();

            LiveKitRoomBackend.DisconnectWaitOutcome outcome =
                await LiveKitRoomBackend.WaitForDisconnectCompletionAsync(
                    disconnected.Task,
                    0,
                    CancellationToken.None);

            Assert.AreEqual(LiveKitRoomBackend.DisconnectWaitOutcome.TimedOut, outcome);
        }
    }
}
