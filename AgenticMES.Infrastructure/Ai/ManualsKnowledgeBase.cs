namespace AgenticMES.Infrastructure.Ai;

/// <summary>
/// Simple in-memory RAG-lite knowledge base containing error code mappings to diagnostic procedures
/// from machine user manuals. In production, this would be backed by a vector database (Qdrant, Pinecone)
/// or Azure AI Search with semantic ranking.
/// </summary>
public sealed class ManualsKnowledgeBase
{
    private readonly Dictionary<string, ErrorCodeEntry> _errorCodes;

    public ManualsKnowledgeBase()
    {
        _errorCodes = new Dictionary<string, ErrorCodeEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["THERMAL_RUNAWAY"] = new ErrorCodeEntry(
                ErrorCode: "THERMAL_RUNAWAY",
                Title: "Spindle Thermal Runaway",
                Severity: "CRITICAL",
                TypicalCause: "Spindle bearing failure, insufficient coolant flow, or coolant pump malfunction causing rapid temperature escalation",
                DiagnosticSteps: new[]
                {
                    "1. IMMEDIATE: Stop machine operation to prevent spindle damage",
                    "2. Verify coolant system pressure and flow rate (target: 15-20 PSI, 8-12 GPM)",
                    "3. Inspect coolant tank level and concentration (target: 5-10% emulsion)",
                    "4. Check spindle bearing condition via audible inspection and vibration sensor data",
                    "5. Review temperature trend over last 30 minutes - gradual rise suggests coolant issue, sudden spike indicates bearing failure",
                    "6. Inspect spindle motor encoder and temperature sensor calibration"
                },
                RecommendedActions: new[]
                {
                    "If temperature > 120°C: Immediate shutdown and spindle inspection required",
                    "If coolant system failure: Refill/adjust coolant, verify pump operation, resume with reduced feed rate",
                    "If bearing wear suspected: Schedule spindle rebuild, reroute work order to alternative machine",
                    "Monitor scrap rate - values > 15% indicate part quality degradation requiring 100% inspection"
                },
                MachineSeries: "Haas CNC Lathe ST Series",
                ManualReference: "Haas ST-20Y Maintenance Manual Section 8.4 - Spindle Thermal Management"),

            ["VIBRATION_ALARM"] = new ErrorCodeEntry(
                ErrorCode: "VIBRATION_ALARM",
                Title: "Excessive Machine Vibration Detected",
                Severity: "HIGH",
                TypicalCause: "Tool wear, unbalanced workpiece, loose fixturing, or structural resonance at specific RPM ranges",
                DiagnosticSteps: new[]
                {
                    "1. Verify vibration sensor threshold and current reading (target: < 0.5 in/sec RMS)",
                    "2. Inspect current tool for wear, chipping, or improper seating in tool holder",
                    "3. Check workpiece clamping and chuck jaw condition",
                    "4. Review spindle RPM vs. vibration correlation - resonance typically occurs at specific speeds",
                    "5. Examine machine foundation and leveling (use precision level, max 0.0005\" per foot)",
                    "6. Verify axis backlash and ball screw preload settings"
                },
                RecommendedActions: new[]
                {
                    "If vibration > 1.0 in/sec: Stop machining immediately to prevent part damage",
                    "If tool wear detected: Execute automatic tool change, resume operation",
                    "If workpiece unbalanced: Re-clamp workpiece, adjust chuck pressure (80-100 PSI)",
                    "If resonance detected: Adjust spindle RPM ±10% to exit resonance band, update G-code if persistent",
                    "Monitor surface finish quality - vibration causes poor Ra values and dimensional deviation"
                },
                MachineSeries: "Haas CNC Mill VF Series",
                ManualReference: "Haas VF-3 Service Manual Section 12.2 - Vibration Analysis and Mitigation"),

            ["HYDRAULIC_PRESSURE_LOW"] = new ErrorCodeEntry(
                ErrorCode: "HYDRAULIC_PRESSURE_LOW",
                Title: "Hydraulic System Pressure Below Threshold",
                Severity: "HIGH",
                TypicalCause: "Hydraulic pump wear, fluid leak, contaminated fluid, or pressure relief valve failure",
                DiagnosticSteps: new[]
                {
                    "1. Check hydraulic pressure gauge reading (target: 800-1000 PSI for clamping operations)",
                    "2. Verify hydraulic fluid reservoir level (minimum 75% capacity)",
                    "3. Inspect all hydraulic lines, fittings, and seals for visible leaks",
                    "4. Check hydraulic fluid temperature (operating range: 90-120°F, alarm > 140°F)",
                    "5. Test pressure relief valve operation and set point (factory default: 1200 PSI)",
                    "6. Examine hydraulic filter condition - replace if differential pressure > 25 PSI"
                },
                RecommendedActions: new[]
                {
                    "If pressure < 600 PSI: Do not attempt machining - chuck clamping force insufficient",
                    "If fluid leak detected: Shutdown machine, repair leak, refill reservoir with ISO VG 46 hydraulic oil",
                    "If pump failure suspected: Replace hydraulic pump assembly, flush system, replace filter",
                    "If temperature alarm: Allow 30-minute cooldown period, verify cooling fan operation",
                    "After repair: Run 3-cycle clamp/unclamp test to verify holding force before production"
                },
                MachineSeries: "All Haas CNC Equipment",
                ManualReference: "Haas Hydraulic Systems Manual Section 6.1 - Pressure System Diagnostics"),

            ["SERVO_FOLLOWING_ERROR"] = new ErrorCodeEntry(
                ErrorCode: "SERVO_FOLLOWING_ERROR",
                Title: "Axis Servo Following Error Exceeded",
                Severity: "MEDIUM",
                TypicalCause: "Mechanical binding, excessive cutting forces, servo tuning drift, or ball screw contamination",
                DiagnosticSteps: new[]
                {
                    "1. Identify which axis reported the error (X, Y, Z) from alarm history",
                    "2. Manually jog affected axis through full travel range, listen for binding or grinding",
                    "3. Inspect ball screw and linear guide ways for contamination, damage, or inadequate lubrication",
                    "4. Review commanded vs. actual position error threshold (typical limit: 0.001\" for 3 seconds)",
                    "5. Check servo motor encoder feedback signal quality and cable connections",
                    "6. Examine cutting parameters - excessive depth of cut or feed rate can overload axis"
                },
                RecommendedActions: new[]
                {
                    "If mechanical binding detected: Clean and lubricate axis way, verify gibs adjustment",
                    "If cutting forces excessive: Reduce depth of cut by 30%, reduce feed rate by 20%, retry operation",
                    "If servo tuning suspected: Restore factory servo parameters from backup, re-tune if problem persists",
                    "If encoder failure: Replace servo motor encoder, re-home machine, verify position accuracy with test indicator",
                    "After resolution: Run axis calibration routine, verify repeatability < 0.0002\""
                },
                MachineSeries: "Haas CNC Mill & Lathe Series",
                ManualReference: "Haas Servo Systems Manual Section 9.3 - Following Error Troubleshooting"),

            ["TOOL_BREAKAGE_DETECTED"] = new ErrorCodeEntry(
                ErrorCode: "TOOL_BREAKAGE_DETECTED",
                Title: "Cutting Tool Breakage or Wear Limit Exceeded",
                Severity: "MEDIUM",
                TypicalCause: "Excessive tool wear, improper cutting parameters, coolant delivery failure, or material hardness variation",
                DiagnosticSteps: new[]
                {
                    "1. Check tool wear monitoring system - verify current tool life percentage",
                    "2. Inspect broken tool in spindle - document tool number, operation number, and cycle count at failure",
                    "3. Examine workpiece for tool damage evidence (gouges, chatter marks, dimensional errors)",
                    "4. Review cutting parameters for this operation vs. recommended values in tool manufacturer data",
                    "5. Verify coolant delivery to cutting zone - check nozzle position and flow rate",
                    "6. Measure workpiece material hardness if multiple tools failing on same part (target: HRC 28-32 for steel)"
                },
                RecommendedActions: new[]
                {
                    "Execute automatic tool change to backup tool in magazine",
                    "If backup tool unavailable: Pause production, manually load replacement tool, update tool offset table",
                    "If material hardness out of spec: Quarantine remaining material lot, notify quality department",
                    "If coolant issue: Adjust nozzle position, increase coolant flow rate, verify flood vs. mist setting",
                    "Update tool life database with actual failure count for predictive maintenance model",
                    "If recurring on same operation: Review G-code for optimization - reduce feed rate or increase spindle RPM"
                },
                MachineSeries: "All Haas CNC Machining Centers",
                ManualReference: "Haas Tool Management Manual Section 5.7 - Tool Breakage Response Protocol")
        };
    }

    /// <summary>
    /// Searches the knowledge base for diagnostic information matching the given error code or symptom keywords.
    /// Returns detailed troubleshooting procedures and recommended actions.
    /// </summary>
    /// <param name="errorCodeOrSymptom">Error code (e.g., THERMAL_RUNAWAY) or symptom keywords (e.g., "high temperature", "vibration")</param>
    /// <returns>Formatted diagnostic guidance or null if no match found</returns>
    public string? SearchDiagnostics(string errorCodeOrSymptom)
    {
        if (string.IsNullOrWhiteSpace(errorCodeOrSymptom))
        {
            return null;
        }

        var searchKey = errorCodeOrSymptom.Trim().ToUpperInvariant().Replace(" ", "_");

        // Direct error code match
        if (_errorCodes.TryGetValue(searchKey, out var entry))
        {
            return FormatDiagnosticEntry(entry);
        }

        // Keyword-based fuzzy search for symptoms
        var keywordMatch = _errorCodes.Values.FirstOrDefault(e =>
            e.Title.Contains(errorCodeOrSymptom, StringComparison.OrdinalIgnoreCase) ||
            e.TypicalCause.Contains(errorCodeOrSymptom, StringComparison.OrdinalIgnoreCase));

        if (keywordMatch is not null)
        {
            return FormatDiagnosticEntry(keywordMatch);
        }

        return null;
    }

    /// <summary>
    /// Retrieves all available error codes and their titles for agent context awareness.
    /// Useful for the AI agent to understand what diagnostics are available.
    /// </summary>
    public string GetAvailableErrorCodes()
    {
        var codes = string.Join("\n", _errorCodes.Values
            .OrderBy(e => e.Severity switch
            {
                "CRITICAL" => 0,
                "HIGH" => 1,
                "MEDIUM" => 2,
                _ => 3
            })
            .Select(e => $"- {e.ErrorCode} ({e.Severity}): {e.Title}"));

        return $"Available Error Codes in Knowledge Base:\n{codes}";
    }

    private static string FormatDiagnosticEntry(ErrorCodeEntry entry)
    {
        var diagnostics = string.Join("\n", entry.DiagnosticSteps);
        var actions = string.Join("\n", entry.RecommendedActions);

        return $"""
                ERROR CODE: {entry.ErrorCode}
                TITLE: {entry.Title}
                SEVERITY: {entry.Severity}
                MACHINE SERIES: {entry.MachineSeries}
                
                TYPICAL CAUSE:
                {entry.TypicalCause}
                
                DIAGNOSTIC STEPS:
                {diagnostics}
                
                RECOMMENDED ACTIONS:
                {actions}
                
                REFERENCE: {entry.ManualReference}
                """;
    }

    private sealed record ErrorCodeEntry(
        string ErrorCode,
        string Title,
        string Severity,
        string TypicalCause,
        string[] DiagnosticSteps,
        string[] RecommendedActions,
        string MachineSeries,
        string ManualReference);
}
