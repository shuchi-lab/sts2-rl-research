namespace Sts2Emulator.Core;

public readonly record struct PowerDef(
    int Id,
    string Name,
    bool IsBuff,
    string StackType,
    bool TicksDown
);
