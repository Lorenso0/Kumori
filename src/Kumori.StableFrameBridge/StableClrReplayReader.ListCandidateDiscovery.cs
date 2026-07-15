using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;

namespace Kumori.Native;

public sealed partial class StableClrReplayReader
{
    private static bool isStructurallyReplayList(ClrType? type)
        => type is not null
           && type.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true
           && tryGetReplayFrameShape(type, out bool replayFrameShape)
           && replayFrameShape;

    private static bool isGenericList(ClrType? type)
        => type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true;

    private static bool tryGetReplayFrameShape(ClrType listType, out bool replayFrameShape)
    {
        replayFrameShape = false;
        try
        {
            ClrType? elementType = listType.GetFieldByName("_items")?.Type?.ComponentType;
            if (elementType is null)
                return false;

            ClrInstanceField[] fields = elementType.Fields.ToArray();
            int floats = fields.Count(field => field.ElementType == ClrElementType.Float);
            int integers = fields.Count(field =>
                field.ElementType is ClrElementType.Int32 or ClrElementType.UInt32
                || field.Type?.IsEnum == true);
            int booleans = fields.Count(field => field.ElementType == ClrElementType.Boolean);
            replayFrameShape = StableGraphDiscoveryPolicy.IsReplayFrameFieldShape(
                fields.Length,
                floats,
                integers,
                booleans);
            return true;
        }
        catch
        {
            // Missing metadata must preserve the old value-based fallback.
            return false;
        }
    }

    private enum StableListCandidateStep { Pending, NotMatched, Matched }

    /// <summary>
    /// Incremental validation for one List&lt;T&gt;. Sampling object addresses and
    /// reading candidate frame fields are CLR operations too, so they share the
    /// graph poll's deadline instead of hiding an unbounded inner loop.
    /// </summary>
    private sealed class StableListCandidateDiscovery
    {
        private enum Phase
        {
            ReadList,
            ReadItems,
            ReadSampleAddresses,
            MaterializeSamples,
            PrepareFields,
            ReadIntValues,
            ReadFloatValues,
            ComputeIntValidity,
            ComputeFloatValidity,
            MatchFields,
            ReadTailAddress,
            ReadTailObject,
            ReadTailTime,
            Completed,
        }

        private readonly StableClrReplayReader reader;
        private readonly ClrObject list;
        private Phase phase;
        private int size;
        private ClrArray array;
        private int[] sampleIndices = [];
        private ulong[] sampleAddresses = [];
        private readonly List<ClrObject> samples = [];
        private int sampleIndex;
        private ClrType? frameType;
        private ClrInstanceField[] allFields = [];
        private ClrInstanceField[] intFields = [];
        private ClrInstanceField[] floatFields = [];
        private int[][] intValues = [];
        private float[][] floatValues = [];
        private bool[] intReadable = [];
        private bool[] floatReadable = [];
        private bool[] timeValid = [];
        private bool[] buttonsValid = [];
        private bool[] coordinateValid = [];
        private int fieldIndex;
        private int fieldSampleIndex;
        private int validityIndex;
        private int timeIndex;
        private int buttonsIndex;
        private int xIndex;
        private int yIndex;
        private string selectedTime = "";
        private string selectedButtons = "";
        private string selectedX = "";
        private string selectedY = "";
        private ulong tailAddress;
        private ClrObject tailObject;
        private StableListCandidateStep completedStep;

        public StableListCandidateDiscovery(StableClrReplayReader reader, ClrObject list, int depth)
        {
            this.reader = reader;
            this.list = list;
            Depth = depth;
        }

        public ClrObject Object => list;
        public int Depth { get; }
        public bool Populated { get; private set; }
        public bool MetadataShaped { get; private set; }
        public bool FrameShaped { get; private set; }
        public StableReplayListCandidate Result { get; private set; }

        public StableListCandidateStep ScanStep(Stopwatch timer, ref int operations)
        {
            if (phase == Phase.Completed)
                return completedStep;

            while (timer.Elapsed < StableGraphDiscoveryPolicy.MaximumPollDuration
                   && operations < StableGraphDiscoveryPolicy.MaximumCandidateOperationsPerPoll)
            {
                switch (phase)
                {
                    case Phase.ReadList:
                        operations++;
                        if (!tryRead(list, "_size", out size) || size < 2 || size > 1_000_000)
                            return complete(StableListCandidateStep.NotMatched);
                        Populated = true;
                        phase = Phase.ReadItems;
                        break;

                    case Phase.ReadItems:
                        operations++;
                        if (!tryReadObject(list, "_items", out ClrObject items) || items.IsNull || !items.IsArray)
                            return complete(StableListCandidateStep.NotMatched);
                        array = items.AsArray();
                        int count = Math.Min(size, 64);
                        sampleIndices = new int[count];
                        sampleAddresses = new ulong[count];
                        for (int index = 0; index < count; index++)
                        {
                            sampleIndices[index] = count == size
                                ? index
                                : (int)Math.Round(index * (size - 1d) / (count - 1d));
                        }
                        phase = Phase.ReadSampleAddresses;
                        break;

                    case Phase.ReadSampleAddresses:
                        if (sampleIndex >= sampleIndices.Length)
                        {
                            sampleIndex = 0;
                            phase = Phase.MaterializeSamples;
                            break;
                        }
                        operations++;
                        try { sampleAddresses[sampleIndex] = reader.readObjectAddressAt(array, sampleIndices[sampleIndex]); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        sampleIndex++;
                        break;

                    case Phase.MaterializeSamples:
                        if (sampleIndex >= sampleAddresses.Length)
                        {
                            if (samples.Count < 2)
                                return complete(StableListCandidateStep.NotMatched);
                            phase = Phase.PrepareFields;
                            break;
                        }
                        operations++;
                        ulong address = sampleAddresses[sampleIndex++];
                        if (address == 0)
                            break;
                        try
                        {
                            ClrObject sample = reader.runtime.Heap.GetObject(address);
                            if (!sample.IsNull)
                                samples.Add(sample);
                        }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        break;

                    case Phase.PrepareFields:
                        operations++;
                        try
                        {
                            frameType = samples[0].Type;
                            if (frameType is null || samples.Any(sample => sample.Type != frameType))
                                return complete(StableListCandidateStep.NotMatched);
                            allFields = frameType.Fields.ToArray();
                        }
                        catch
                        {
                            return complete(StableListCandidateStep.NotMatched);
                        }
                        floatFields = allFields.Where(field => field.ElementType == ClrElementType.Float).ToArray();
                        intFields = allFields.Where(field => field.ElementType is ClrElementType.Int32 or ClrElementType.UInt32 || field.Type?.IsEnum == true).ToArray();
                        int booleans = allFields.Count(field => field.ElementType == ClrElementType.Boolean);
                        // Stable's replay frame is a small leaf object: X/Y,
                        // input flags, button-state enum and timestamp.
                        if (!StableGraphDiscoveryPolicy.IsReplayFrameFieldShape(
                                allFields.Length,
                                floatFields.Length,
                                intFields.Length,
                                booleans))
                            return complete(StableListCandidateStep.NotMatched);
                        MetadataShaped = true;

                        intValues = new int[intFields.Length][];
                        floatValues = new float[floatFields.Length][];
                        intReadable = new bool[intFields.Length];
                        floatReadable = new bool[floatFields.Length];
                        timeValid = new bool[intFields.Length];
                        buttonsValid = new bool[intFields.Length];
                        coordinateValid = new bool[floatFields.Length];
                        for (int index = 0; index < intFields.Length; index++)
                            intValues[index] = new int[samples.Count];
                        for (int index = 0; index < floatFields.Length; index++)
                            floatValues[index] = new float[samples.Count];
                        Array.Fill(intReadable, true);
                        Array.Fill(floatReadable, true);
                        phase = Phase.ReadIntValues;
                        break;

                    case Phase.ReadIntValues:
                        if (fieldIndex >= intFields.Length)
                        {
                            fieldIndex = 0;
                            fieldSampleIndex = 0;
                            phase = Phase.ReadFloatValues;
                            break;
                        }
                        operations++;
                        try
                        {
                            intValues[fieldIndex][fieldSampleIndex] = samples[fieldSampleIndex]
                                .ReadField<int>(intFields[fieldIndex].Name!);
                            if (++fieldSampleIndex >= samples.Count)
                            {
                                fieldIndex++;
                                fieldSampleIndex = 0;
                            }
                        }
                        catch
                        {
                            intReadable[fieldIndex] = false;
                            fieldIndex++;
                            fieldSampleIndex = 0;
                        }
                        break;

                    case Phase.ReadFloatValues:
                        if (fieldIndex >= floatFields.Length)
                        {
                            validityIndex = 0;
                            phase = Phase.ComputeIntValidity;
                            break;
                        }
                        operations++;
                        try
                        {
                            floatValues[fieldIndex][fieldSampleIndex] = samples[fieldSampleIndex]
                                .ReadField<float>(floatFields[fieldIndex].Name!);
                            if (++fieldSampleIndex >= samples.Count)
                            {
                                fieldIndex++;
                                fieldSampleIndex = 0;
                            }
                        }
                        catch
                        {
                            floatReadable[fieldIndex] = false;
                            fieldIndex++;
                            fieldSampleIndex = 0;
                        }
                        break;

                    case Phase.ComputeIntValidity:
                        if (validityIndex >= intFields.Length)
                        {
                            validityIndex = 0;
                            phase = Phase.ComputeFloatValidity;
                            break;
                        }
                        operations++;
                        if (intReadable[validityIndex])
                        {
                            timeValid[validityIndex] = isTimeSeries(intValues[validityIndex]);
                            buttonsValid[validityIndex] = intValues[validityIndex].All(value => (value & ~0x1f) == 0);
                        }
                        validityIndex++;
                        break;

                    case Phase.ComputeFloatValidity:
                        if (validityIndex >= floatFields.Length)
                        {
                            phase = Phase.MatchFields;
                            break;
                        }
                        operations++;
                        coordinateValid[validityIndex] = floatReadable[validityIndex]
                            && floatValues[validityIndex].All(value => float.IsFinite(value) && value is >= -10_000 and <= 10_000);
                        validityIndex++;
                        break;

                    case Phase.MatchFields:
                        if (timeIndex >= intFields.Length)
                            return complete(StableListCandidateStep.NotMatched);
                        operations++;
                        int currentTime = timeIndex;
                        int currentButtons = buttonsIndex;
                        int currentX = xIndex;
                        int currentY = yIndex;
                        advanceFieldCombination();
                        if (currentTime == currentButtons || currentX == currentY
                            || !timeValid[currentTime] || !buttonsValid[currentButtons]
                            || !coordinateValid[currentX] || !coordinateValid[currentY])
                            break;
                        selectedTime = intFields[currentTime].Name!;
                        selectedButtons = intFields[currentButtons].Name!;
                        selectedX = floatFields[currentX].Name!;
                        selectedY = floatFields[currentY].Name!;
                        FrameShaped = true;
                        phase = Phase.ReadTailAddress;
                        break;

                    case Phase.ReadTailAddress:
                        operations++;
                        try { tailAddress = reader.readObjectAddressAt(array, size - 1); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        if (tailAddress == 0)
                            return complete(StableListCandidateStep.NotMatched);
                        phase = Phase.ReadTailObject;
                        break;

                    case Phase.ReadTailObject:
                        operations++;
                        try { tailObject = reader.runtime.Heap.GetObject(tailAddress); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        if (tailObject.IsNull)
                            return complete(StableListCandidateStep.NotMatched);
                        phase = Phase.ReadTailTime;
                        break;

                    case Phase.ReadTailTime:
                        operations++;
                        int lastTime;
                        try { lastTime = tailObject.ReadField<int>(selectedTime); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        string layout = string.Join(",", allFields.Select(field => $"{field.ElementType}@{field.Offset}"));
                        Result = new StableReplayListCandidate(
                            list.Address,
                            size,
                            lastTime,
                            selectedTime,
                            selectedX,
                            selectedY,
                            selectedButtons,
                            frameType?.Name ?? "unknown",
                            layout);
                        return complete(StableListCandidateStep.Matched);

                    case Phase.Completed:
                        return completedStep;
                }
            }
            return StableListCandidateStep.Pending;
        }

        private void advanceFieldCombination()
        {
            if (++yIndex < floatFields.Length)
                return;
            yIndex = 0;
            if (++xIndex < floatFields.Length)
                return;
            xIndex = 0;
            if (++buttonsIndex < intFields.Length)
                return;
            buttonsIndex = 0;
            timeIndex++;
        }

        private static bool isTimeSeries(IReadOnlyList<int> values)
        {
            int orderedPairs = 0;
            int minimum = int.MaxValue;
            int maximum = int.MinValue;
            for (int index = 0; index < values.Count; index++)
            {
                int value = values[index];
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                if (index + 1 < values.Count && value <= values[index + 1])
                    orderedPairs++;
            }
            return orderedPairs >= values.Count - 2 && (long)maximum - minimum >= 10;
        }

        private StableListCandidateStep complete(StableListCandidateStep result)
        {
            completedStep = result;
            phase = Phase.Completed;
            return result;
        }
    }
}
