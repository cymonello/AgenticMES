# Equipment state machine

ISA-95 transitions live here. The Domain `Equipment` aggregate stores state; it does not decide legal moves.

| Type | Role |
|---|---|
| `EquipmentStateMachineDefinition` | Stateless graph (`Permit` table) |
| `EquipmentStateMachineFactory` | DI: bind an `Equipment` instance |
| `EquipmentStateMachineService` | `CanFire` / `Fire` |

Application depends on `IEquipmentStateMachineFactory` / `IEquipmentStateMachine` only.

```
Idle  → Start, EnterSetup, EnterMaintenance, Fault
Running → Stop, Fault, EnterMaintenance, EnterSetup
Faulted → Reset, EnterMaintenance
Maintenance → Reset
Setup → Reset, CompleteSetup, Fault
```

DOT graph: `EquipmentStateMachineDefinition.ToDotGraph()`.
