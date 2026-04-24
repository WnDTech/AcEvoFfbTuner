namespace AcEvoFfbTuner.Core.SharedMemory.Structs;

public enum AcEvoStatus : int
{
    AcOff = 0,
    AcReplay = 1,
    AcLive = 2,
    AcPause = 3
}

public enum AcEvoSessionType : int
{
    AcUnknown = -1,
    AcPractice = 0,
    AcQualify = 1,
    AcRace = 2,
    AcHotlap = 3,
    AcTimeAttack = 4,
    AcDrift = 5,
    AcDrag = 6
}

public enum AcEvoFlagType : int
{
    AcNoFlag = 0,
    AcBlueFlag = 1,
    AcYellowFlag = 2,
    AcBlackFlag = 3,
    AcWhiteFlag = 4,
    AcCheckeredFlag = 5,
    AcPenaltyFlag = 6,
    AcGreenFlag = 7,
    AcOrangeFlag = 8
}

public enum AcEvoCarLocation : int
{
    AcevoNone = 0,
    AcevoTrack = 1,
    AcevoPitlane = 2,
    AcevoPitbox = 3
}

public enum AcEvoEngineType : int
{
    AcevoInternalCombustion = 0,
    AcevoElectricMotor = 1,
    AcevoHybrid = 2
}

public enum AcEvoStartingGrip : int
{
    AcevoGreen = 0,
    AcevoFast = 1,
    AcevoOptimum = 2
}
