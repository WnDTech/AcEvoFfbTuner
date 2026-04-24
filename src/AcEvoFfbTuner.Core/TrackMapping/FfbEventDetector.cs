using System.Diagnostics;

namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class FfbEventDetector
{
    private float _prevForce;
    private float _prevSteerAngle;
    private float _prevSpeed;
    private float _prevBrake;
    private float _prevGas;
    private int _prevWaypointIdx = -1;
    private bool _prevInCorner;
    private string? _prevCornerName;
    private double _startTime;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private float _prevMzFront, _prevFxFront, _prevFyFront;
    private float _prevRoadMod;
    private int _steerSignStableTicks;
    private const int SteerStableRequired = 10;

    private const float SnapDeltaThreshold = 0.08f;
    private const float HighSpeedSnapThreshold = 0.12f;
    private const int OscillationWindow = 10;
    private const int OscillationSignFlipThreshold = 3;
    private const float SteerActiveThreshold = 0.02f;
    private const float UndersteerSlipAngleThreshold = 5.0f;
    private const float OversteerSlipDeltaThreshold = 3.0f;
    private const float MinForceForEvents = 0.01f;

    private readonly float[] _recentDeltas = new float[OscillationWindow];
    private int _deltaIdx;
    private double _lastOscillationTime;

    public FfbDiagnosticEvent? LastEvent { get; private set; }

    public List<FfbDiagnosticEvent> DetectEvents(
        float outputForce,
        float steerAngle,
        float speedKmh,
        float slipAngleFront,
        float slipRatioFront,
        float lateralG,
        float brakeInput,
        float gasInput,
        bool inCorner,
        string? cornerName,
        int waypointIdx,
        bool isClipping,
        float mzFront = 0f,
        float fxFront = 0f,
        float fyFront = 0f,
        float postCompression = 0f,
        float postSlip = 0f,
        float postDamping = 0f,
        float postDynamic = 0f,
        float roadForceModulation = 0f)
    {
        var events = new List<FfbDiagnosticEvent>();
        double now = _sw.Elapsed.TotalSeconds;

        if (_prevWaypointIdx < 0)
        {
            _prevForce = outputForce;
            _prevSteerAngle = steerAngle;
            _prevSpeed = speedKmh;
            _prevBrake = brakeInput;
            _prevGas = gasInput;
            _prevWaypointIdx = waypointIdx;
            _prevInCorner = inCorner;
            _prevCornerName = cornerName;
            _prevRoadMod = roadForceModulation;
            _startTime = now;
            return events;
        }

        float forceDelta = outputForce - _prevForce;
        float absDelta = MathF.Abs(forceDelta);
        float absSteer = MathF.Abs(steerAngle);
        float absForce = MathF.Abs(outputForce);

        bool forceSignificant = absForce > MinForceForEvents ||
            MathF.Abs(_prevForce) > MinForceForEvents;

        var drivingState = ClassifyDrivingState(
            steerAngle, _prevSteerAngle, speedKmh, _prevSpeed,
            brakeInput, _prevBrake, gasInput, _prevGas,
            slipAngleFront, lateralG, inCorner);

        if (_prevWaypointIdx >= 0)
        {
            bool prevSign = _prevSteerAngle >= 0f;
            bool curSign = steerAngle >= 0f;
            if (prevSign == curSign && absSteer > SteerActiveThreshold)
                _steerSignStableTicks++;
            else
        _prevRoadMod = 0f;
        _steerSignStableTicks = 0;
        }

        if (!forceSignificant)
        {
            _prevForce = outputForce;
            _prevSteerAngle = steerAngle;
            _prevSpeed = speedKmh;
            _prevBrake = brakeInput;
            _prevGas = gasInput;
            _prevWaypointIdx = waypointIdx;
            _prevInCorner = inCorner;
            _prevCornerName = cornerName;
            _prevMzFront = mzFront;
            _prevFxFront = fxFront;
            _prevFyFront = fyFront;
            _recentDeltas[_deltaIdx % OscillationWindow] = 0f;
            _deltaIdx++;
            return events;
        }

        _recentDeltas[_deltaIdx % OscillationWindow] = forceDelta;
        _deltaIdx++;

        if (isClipping && speedKmh > 20f)
        {
            var cls = ClassifyEvent(FfbEventType.Clipping, absDelta, speedKmh,
                absSteer, inCorner, slipAngleFront, drivingState,
                roadForceModulation, _prevRoadMod);
            events.Add(new FfbDiagnosticEvent
            {
                EventType = FfbEventType.Clipping,
                Classification = cls,
                DrivingState = drivingState,
                ForceDelta = forceDelta,
                OutputForce = outputForce,
                SteerAngle = steerAngle,
                SpeedKmh = speedKmh,
                SlipAngleFront = slipAngleFront,
                SlipRatioFront = slipRatioFront,
                InCorner = inCorner,
                CornerName = cornerName,
                WaypointIndex = waypointIdx,
                TimestampS = now - _startTime
            });
        }

        float snapThreshold = speedKmh > 150f ? HighSpeedSnapThreshold : SnapDeltaThreshold;
        if (absDelta > snapThreshold && speedKmh > 5f)
        {
            var cls = ClassifyEvent(FfbEventType.Snap, absDelta, speedKmh,
                absSteer, inCorner, slipAngleFront, drivingState,
                roadForceModulation, _prevRoadMod);

            events.Add(new FfbDiagnosticEvent
            {
                EventType = FfbEventType.Snap,
                Classification = cls,
                DrivingState = drivingState,
                ForceDelta = forceDelta,
                OutputForce = outputForce,
                SteerAngle = steerAngle,
                SpeedKmh = speedKmh,
                SlipAngleFront = slipAngleFront,
                SlipRatioFront = slipRatioFront,
                InCorner = inCorner,
                CornerName = cornerName,
                WaypointIndex = waypointIdx,
                TimestampS = now - _startTime
            });
        }

        if (_deltaIdx >= OscillationWindow && (now - _lastOscillationTime) > 0.5)
        {
            int signFlips = 0;
            for (int i = 1; i < OscillationWindow; i++)
            {
                int curIdx = (_deltaIdx - i) % OscillationWindow;
                int prevIdx = (_deltaIdx - i - 1) % OscillationWindow;
                if (curIdx < 0) curIdx += OscillationWindow;
                if (prevIdx < 0) prevIdx += OscillationWindow;
                if (_recentDeltas[curIdx] * _recentDeltas[prevIdx] < 0f)
                    signFlips++;
            }

            if (signFlips >= OscillationSignFlipThreshold)
            {
                _lastOscillationTime = now;
                var cls = ClassifyEvent(FfbEventType.OscillationCluster, absDelta, speedKmh,
                    absSteer, inCorner, slipAngleFront, drivingState,
                    roadForceModulation, _prevRoadMod);

                events.Add(new FfbDiagnosticEvent
                {
                    EventType = FfbEventType.OscillationCluster,
                    Classification = cls,
                    DrivingState = drivingState,
                    ForceDelta = forceDelta,
                    OutputForce = outputForce,
                    SteerAngle = steerAngle,
                    SpeedKmh = speedKmh,
                    SlipAngleFront = slipAngleFront,
                    SlipRatioFront = slipRatioFront,
                    InCorner = inCorner,
                    CornerName = cornerName,
                    WaypointIndex = waypointIdx,
                    TimestampS = now - _startTime
                });
            }
        }

        if (speedKmh > 30f && absSteer > SteerActiveThreshold && absDelta > 0.03f
            && _steerSignStableTicks >= SteerStableRequired)
        {
            bool forceOpposesSteer = (outputForce > 0f && steerAngle < 0f) ||
                                      (outputForce < 0f && steerAngle > 0f);
            bool forceWithSteer = (outputForce > 0f && steerAngle > 0f) ||
                                   (outputForce < 0f && steerAngle < 0f);

            if (forceWithSteer && MathF.Abs(outputForce) > 0.3f)
            {
                var cls = ClassifyEvent(FfbEventType.ForceDirectionAnomaly, absDelta, speedKmh,
                    absSteer, inCorner, slipAngleFront, drivingState,
                    roadForceModulation, _prevRoadMod);

                if (cls >= FfbEventClassification.Suspicious)
                {
                    events.Add(new FfbDiagnosticEvent
                    {
                        EventType = FfbEventType.ForceDirectionAnomaly,
                        Classification = cls,
                        DrivingState = drivingState,
                        ForceDelta = forceDelta,
                        OutputForce = outputForce,
                        SteerAngle = steerAngle,
                        SpeedKmh = speedKmh,
                        SlipAngleFront = slipAngleFront,
                        SlipRatioFront = slipRatioFront,
                        InCorner = inCorner,
                        CornerName = cornerName,
                        WaypointIndex = waypointIdx,
                        TimestampS = now - _startTime
                    });
                }
            }
        }

        if (events.Count > 0)
        {
            foreach (var evt in events)
            {
                evt.MzFrontForce = mzFront;
                evt.FxFrontForce = fxFront;
                evt.FyFrontForce = fyFront;
                evt.PostCompressionForce = postCompression;
                evt.PostSlipForce = postSlip;
                evt.PostDampingForce = postDamping;
                evt.PostDynamicForce = postDynamic;
                evt.PrevMzFrontForce = _prevMzFront;
                evt.PrevFxFrontForce = _prevFxFront;
                evt.PrevFyFrontForce = _prevFyFront;
                evt.RoadForceModulation = roadForceModulation;
                evt.PrevRoadForceModulation = _prevRoadMod;
            }
            LastEvent = events[^1];
        }

        _prevForce = outputForce;
        _prevSteerAngle = steerAngle;
        _prevSpeed = speedKmh;
        _prevBrake = brakeInput;
        _prevGas = gasInput;
        _prevWaypointIdx = waypointIdx;
        _prevInCorner = inCorner;
        _prevCornerName = cornerName;
        _prevMzFront = mzFront;
        _prevFxFront = fxFront;
        _prevFyFront = fyFront;
        _prevRoadMod = roadForceModulation;

        return events;
    }

    private static DrivingState ClassifyDrivingState(
        float steerAngle, float prevSteerAngle,
        float speedKmh, float prevSpeed,
        float brake, float prevBrake,
        float gas, float prevGas,
        float slipAngleFront, float lateralG, bool inCorner)
    {
        float absSteer = MathF.Abs(steerAngle);
        float steerDelta = MathF.Abs(steerAngle - prevSteerAngle);
        bool steeringIncreasing = MathF.Abs(steerAngle) > MathF.Abs(prevSteerAngle);
        bool braking = brake > 0.1f;
        bool brakingIncreasing = brake > prevBrake + 0.01f;
        bool accelerating = gas > 0.3f && gas > prevGas + 0.01f;
        float absLatG = MathF.Abs(lateralG);

        if (inCorner)
        {
            if (MathF.Abs(slipAngleFront) > UndersteerSlipAngleThreshold && absSteer > 0.05f)
                return DrivingState.Understeer;

            if (steerDelta > OversteerSlipDeltaThreshold && steeringIncreasing == false && absLatG > 0.5f)
                return DrivingState.Oversteer;

            if (steeringIncreasing && steerDelta > 0.005f)
                return brakingIncreasing ? DrivingState.BrakingIntoCorner : DrivingState.CornerEntry;

            if (!steeringIncreasing && steerDelta > 0.005f)
                return accelerating ? DrivingState.AcceleratingOutOfCorner : DrivingState.CornerExit;

            return DrivingState.CornerApex;
        }

        if (braking && speedKmh > 30f)
            return DrivingState.BrakingIntoCorner;

        if (absSteer < SteerActiveThreshold)
            return DrivingState.StraightCruising;

        return DrivingState.Unknown;
    }

    private static FfbEventClassification ClassifyEvent(
        FfbEventType eventType, float absDelta, float speedKmh,
        float absSteer, bool inCorner, float slipAngleFront,
        DrivingState drivingState,
        float roadForceModulation = 0f, float prevRoadForceModulation = 0f)
    {
        bool isRoadVibration = MathF.Abs(roadForceModulation) > 0.02f ||
            MathF.Abs(roadForceModulation - prevRoadForceModulation) > 0.01f;

        if (eventType == FfbEventType.ForceDirectionAnomaly)
            return FfbEventClassification.Suspicious;

        if (eventType == FfbEventType.OscillationCluster)
        {
            if (isRoadVibration)
                return FfbEventClassification.ExpectedDynamics;
            if (!inCorner && absSteer < SteerActiveThreshold && speedKmh > 60f)
                return FfbEventClassification.Suspicious;
            if (inCorner && (drivingState == DrivingState.CornerApex || drivingState == DrivingState.CornerEntry))
                return FfbEventClassification.ExpectedDynamics;
            return FfbEventClassification.Suspicious;
        }

        if (eventType == FfbEventType.Snap)
        {
            if (isRoadVibration)
                return FfbEventClassification.ExpectedDynamics;

            if (inCorner)
            {
                if (drivingState == DrivingState.CornerEntry || drivingState == DrivingState.CornerExit ||
                    drivingState == DrivingState.BrakingIntoCorner || drivingState == DrivingState.AcceleratingOutOfCorner)
                    return FfbEventClassification.ExpectedDynamics;

                if (drivingState == DrivingState.Understeer || drivingState == DrivingState.Oversteer)
                    return FfbEventClassification.ExpectedDynamics;

                if (MathF.Abs(slipAngleFront) > 3f)
                    return FfbEventClassification.ExpectedDynamics;

                return FfbEventClassification.Normal;
            }

            if (speedKmh < 30f)
                return FfbEventClassification.Normal;

            if (!inCorner && absSteer < SteerActiveThreshold && absDelta > 0.15f)
                return FfbEventClassification.Suspicious;

            if (!inCorner && absSteer > SteerActiveThreshold && MathF.Abs(slipAngleFront) > 2f)
                return FfbEventClassification.Normal;

            return FfbEventClassification.Normal;
        }

        if (eventType == FfbEventType.Clipping)
        {
            if (inCorner && speedKmh > 40f)
                return FfbEventClassification.ExpectedDynamics;
            if (!inCorner && speedKmh > 100f && absSteer < SteerActiveThreshold)
                return FfbEventClassification.Suspicious;
            return FfbEventClassification.Normal;
        }

        return FfbEventClassification.Normal;
    }

    public void Reset()
    {
        _prevWaypointIdx = -1;
        _prevForce = 0f;
        _prevSteerAngle = 0f;
        _prevSpeed = 0f;
        _prevBrake = 0f;
        _prevGas = 0f;
        _deltaIdx = 0;
        _lastOscillationTime = 0;
        _steerSignStableTicks = 0;
        Array.Clear(_recentDeltas);
        LastEvent = null;
        _sw.Restart();
    }
}
