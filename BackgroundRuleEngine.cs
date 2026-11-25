using System;
using System.Threading;

namespace Procesi
{
    internal sealed class BackgroundRuleEngine : IDisposable
    {
        private readonly RuleStore rules;
        private readonly Thread worker;
        private readonly object gate = new object();
        private bool pending = true;
        private bool stopping;

        internal BackgroundRuleEngine(RuleStore rules)
        {
            this.rules = rules;
            worker = new Thread(Run) { IsBackground = true, Name = "Process Panel rules" };
            rules.Changed += Wake;
            worker.Start();
        }

        private void Wake()
        {
            lock (gate)
            {
                pending = true;
                Monitor.Pulse(gate);
            }
        }

        private void Run()
        {
            int delay = Timeout.Infinite;
            while (true)
            {
                lock (gate)
                {
                    if (!pending && !stopping) Monitor.Wait(gate,delay);
                    if (stopping) return;
                    pending = false;
                }
                delay = rules.ScanRunningProcesses() ? 2000 : Timeout.Infinite;
            }
        }

        public void Dispose()
        {
            rules.Changed -= Wake;
            lock (gate)
            {
                stopping = true;
                Monitor.Pulse(gate);
            }
            worker.Join();
        }
    }
}
