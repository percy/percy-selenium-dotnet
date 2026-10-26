using System;
using System.Threading.Tasks;
using Xunit;

namespace PercyIO.Selenium.Tests
{
    public class StateLockTest
    {
        [Fact]
        public void CurrentSessionType_ReflectsSetAndReset()
        {
            Percy.ResetInternalCaches();
            Assert.Null(Percy.CurrentSessionType());

            Percy.setSessionType("automate");
            Assert.Equal("automate", Percy.CurrentSessionType());

            Percy.ResetInternalCaches();
            Assert.Null(Percy.CurrentSessionType());
        }

        [Fact]
        public void ResetInternalCaches_ConcurrentWithSessionTypeReads_SeesOnlyKnownValues()
        {
            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 5000; i++)
                {
                    Percy.setSessionType("automate");
                    Percy.ResetInternalCaches();
                }
            });
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 5000; i++)
                {
                    var type = Percy.CurrentSessionType();
                    Assert.True(type == null || type == "automate");
                }
            });

            Task.WaitAll(writer, reader);
            Percy.ResetInternalCaches();
        }
    }
}
