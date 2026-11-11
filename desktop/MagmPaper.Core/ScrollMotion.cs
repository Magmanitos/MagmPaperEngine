namespace MagmPaper.Core;

// Analytic critically damped motion keeps velocity continuous when wheel events
// arrive mid-animation. Its trajectory does not depend on the display rate.
public sealed class ScrollMotion(double position)
{
    public double Position { get; private set; } = position;
    public double Target { get; private set; } = position;
    public double Velocity { get; private set; }
    public bool Settled => Math.Abs(Target - Position) < .1 && Math.Abs(Velocity) < 4;
    public void MoveTo(double target) { if (double.IsFinite(target)) Target = target; }
    public void Reset(double position) { Position = Target = position; Velocity = 0; }
    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        const double speed = 32;
        double distance = Position - Target, change = Velocity + speed * distance, decay = Math.Exp(-speed * seconds);
        Position = Target + (distance + change * seconds) * decay;
        Velocity = (Velocity - speed * change * seconds) * decay;
    }
}
