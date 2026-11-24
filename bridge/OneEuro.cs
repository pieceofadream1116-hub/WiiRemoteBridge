using System;

namespace WiiRemoteBridge
{
    // "One Euro" filter: smooths heavily when something is nearly still (no jitter) and lightly
    // when it moves fast (no lag). Same idea used by many pointer and VR trackers.
    internal sealed class OneEuro
    {
        readonly double minCutoff, beta, derivCutoff;
        double x, dx;
        bool started;

        public OneEuro(double minCutoff, double beta, double derivCutoff = 1.0)
        {
            this.minCutoff = minCutoff;
            this.beta = beta;
            this.derivCutoff = derivCutoff;
        }

        public void Reset() => started = false;

        public double Filter(double value, double dt)
        {
            if (!started) { x = value; dx = 0; started = true; return value; }
            double rawDx = (value - x) / dt;
            dx += Alpha(derivCutoff, dt) * (rawDx - dx);
            double cutoff = minCutoff + beta * Math.Abs(dx);
            x += Alpha(cutoff, dt) * (value - x);
            return x;
        }

        static double Alpha(double cutoff, double dt)
        {
            double tau = 1.0 / (2 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / dt);
        }
    }
}
