using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.PedalHaptics;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class PhfRouterTests
{
    private static PhfRouter.Mix Route(int mask, params PhfRouter.Effect[] fx) =>
        PhfRouter.Route(fx, _ => mask, 30);

    [Fact]
    public void Defaults_MatchPreviousFixedRouting()
    {
        var c = new MozaPhfConfig();
        c.GetRoute(PhfEffectKeys.Abs).Should().Be(PhfRouter.Brake);
        c.GetRoute(PhfEffectKeys.Tc).Should().Be(PhfRouter.Throttle);
        c.GetRoute(PhfEffectKeys.Kerbs).Should().Be(PhfRouter.Brake | PhfRouter.Throttle);
        c.GetRoute(PhfEffectKeys.Engine).Should().Be(PhfRouter.Throttle | PhfRouter.Clutch);
    }

    [Fact]
    public void Abs_RoutedToThrottle_LeavesBrakeSilent()
    {
        var m = Route(PhfRouter.Throttle, new PhfRouter.Effect(PhfEffectKeys.Abs, 100, 18));
        m.Brake.Should().Be(0);
        m.Throttle.Should().Be(100);
        m.ThrottleHz.Should().Be(18);
    }

    [Fact]
    public void Kerbs_BothPedals_SplitLeftRight()
    {
        var m = Route(PhfRouter.Brake | PhfRouter.Throttle,
            new PhfRouter.Effect(PhfEffectKeys.Kerbs, 0, 45, Left: 80, Right: 0));
        m.Brake.Should().Be(80);
        m.Throttle.Should().Be(0);
    }

    [Fact]
    public void Kerbs_SinglePedal_FeelsEitherSide()
    {
        var m = Route(PhfRouter.Throttle,
            new PhfRouter.Effect(PhfEffectKeys.Kerbs, 0, 45, Left: 80, Right: 20));
        m.Throttle.Should().Be(80);
        m.Brake.Should().Be(0);
    }

    [Fact]
    public void Clutch_UsesClutchValue()
    {
        var m = Route(PhfRouter.Throttle | PhfRouter.Clutch,
            new PhfRouter.Effect(PhfEffectKeys.Engine, 40, 80, ClutchValue: 10));
        m.Throttle.Should().Be(40);
        m.Clutch.Should().Be(10);
    }

    [Fact]
    public void Frequency_FollowsStrongestEffect()
    {
        var m = Route(PhfRouter.Brake,
            new PhfRouter.Effect(PhfEffectKeys.BrakePressure, 30, 25),
            new PhfRouter.Effect(PhfEffectKeys.Abs, 120, 18));
        m.Brake.Should().Be(150);
        m.BrakeHz.Should().Be(18);
    }

    [Fact]
    public void RouteOff_ProducesNothing()
    {
        var m = Route(0, new PhfRouter.Effect(PhfEffectKeys.Abs, 100, 18));
        (m.Brake + m.Throttle + m.Clutch).Should().Be(0);
        m.BrakeHz.Should().Be(30);
    }

    [Fact]
    public void SetRoute_Persists_AndMissingKeyUsesDefault()
    {
        var c = new MozaPhfConfig();
        c.SetRoute(PhfEffectKeys.Abs, PhfRouter.Throttle | PhfRouter.Clutch);
        c.GetRoute(PhfEffectKeys.Abs).Should().Be(0b101);
        c.EffectRoutes.Clear();
        c.GetRoute(PhfEffectKeys.Abs).Should().Be(PhfRouter.Brake);
    }
}
