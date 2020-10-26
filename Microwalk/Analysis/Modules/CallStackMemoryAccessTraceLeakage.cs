﻿using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.Extensions;
using Microwalk.TraceEntryTypes;
using Microwalk.Utilities;
using Standart.Hash.xxHash;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization.TypeInspectors;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("call-stack-memory-access-trace-leakage", "Calculates several trace leakage measures for each call stack of a memory accessing instruction.")]
    internal class CallStackMemoryAccessTraceLeakage : AnalysisStage
    {
        /// <summary>
        /// Maps testcase IDs to call trees.
        /// </summary>
        private readonly ConcurrentDictionary<int, CallTreeNode> _testcaseCallTrees = new ConcurrentDictionary<int, CallTreeNode>();

        /// <summary>
        /// Maps instruction addresses to formatted instructions.
        /// </summary>
        private readonly ConcurrentDictionary<ulong, string> _formattedInstructions = new ConcurrentDictionary<ulong, string>();

        /// <summary>
        /// The output directory for analysis results.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// Output format.
        /// </summary>
        private OutputFormat _outputFormat = OutputFormat.Csv;

        /// <summary>
        /// Controls whether the entire final state should be written to a dump file.
        /// </summary>
        private bool _dumpFullData = false;

        /// <summary>
        /// MAP file collection for resolving symbol names.
        /// </summary>
        private readonly MapFileCollection _mapFileCollection = new MapFileCollection();

        public override bool SupportsParallelism => true;

        public override async Task AddTraceAsync(TraceEntity traceEntity)
        {
            // Trace call tree
            var currentCallTree = new Stack<CallTreeNode>();
            var rootNode = new CallTreeNode
            {
                InstructionId = 0,
                CallStackId = 0, // Starting value
                Hits = 1,
                Parent = null,
                Children = new Dictionary<ulong, CallTreeNode>(),
                InstructionHashes = new Dictionary<ulong, byte[]>()
            };
            currentCallTree.Push(rootNode);

            // Iterate trace entries
            byte[] callStackHashBuffer = new byte[16];
            foreach(var traceEntry in traceEntity.PreprocessedTraceFile)
            {
                // Retrieve current call tree node
                var currentNode = currentCallTree.Peek();
                BinaryPrimitives.WriteUInt64LittleEndian(callStackHashBuffer, currentNode.CallStackId);

                // Handle branch instructions
                if(traceEntry.EntryType == TraceEntryTypes.TraceEntryTypes.Branch)
                {
                    var branch = (Branch)traceEntry;

                    // Only analyze taken branches
                    if(!branch.Taken)
                        continue;

                    // Call or return?
                    if(branch.BranchType == Branch.BranchTypes.Call)
                    {
                        // Retrieve or create node of target instruction
                        ulong targetInstructionId = ((ulong)branch.DestinationImageId << 32) | branch.DestinationInstructionRelativeAddress;
                        if(!currentNode.Children.TryGetValue(targetInstructionId, out var targetNode))
                        {
                            // New node
                            targetNode = new CallTreeNode
                            {
                                InstructionId = targetInstructionId,
                                Hits = 0,
                                Parent = currentNode,
                                Children = new Dictionary<ulong, CallTreeNode>(),
                                InstructionHashes = new Dictionary<ulong, byte[]>()
                            };
                            currentNode.Children.Add(targetInstructionId, targetNode);

                            // Compute call stack ID
                            BinaryPrimitives.WriteUInt64LittleEndian(callStackHashBuffer.AsSpan(8), targetInstructionId);
                            targetNode.CallStackId = xxHash64.ComputeHash(callStackHashBuffer, 16);
                        }

                        // Format instruction
                        StoreFormattedInstruction(targetInstructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branch.DestinationImageId],
                                                  branch.DestinationInstructionRelativeAddress);

                        // This will be the next node
                        ++targetNode.Hits;
                        currentCallTree.Push(targetNode);
                    }
                    else if(branch.BranchType == Branch.BranchTypes.Return)
                    {
                        // Sanity check for unbalanced calls/returns: Never pop the root node
                        if(currentCallTree.Count == 1)
                        {
                            await Logger.LogWarningAsync("Skipping unbalanced return entry in call tree analysis. Results may be incorrect.");
                            continue;
                        }

                        // Go up by one layer
                        currentCallTree.Pop();
                    }

                    continue;
                }

                // This is a memory access
                // Extract instruction and memory address IDs
                ulong instructionId;
                ulong memoryAddressId;
                switch(traceEntry.EntryType)
                {
                    case TraceEntryTypes.TraceEntryTypes.HeapMemoryAccess:
                    {
                        var heapMemoryAccess = (HeapMemoryAccess)traceEntry;
                        instructionId = ((ulong)heapMemoryAccess.InstructionImageId << 32) | heapMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = ((ulong)heapMemoryAccess.MemoryAllocationBlockId << 32) | heapMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[heapMemoryAccess.InstructionImageId],
                                                  heapMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    case TraceEntryTypes.TraceEntryTypes.ImageMemoryAccess:
                    {
                        var imageMemoryAccess = (ImageMemoryAccess)traceEntry;
                        instructionId = ((ulong)imageMemoryAccess.InstructionImageId << 32) | imageMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = ((ulong)imageMemoryAccess.MemoryImageId << 32) | imageMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[imageMemoryAccess.InstructionImageId],
                                                  imageMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    case TraceEntryTypes.TraceEntryTypes.StackMemoryAccess:
                    {
                        var stackMemoryAccess = (StackMemoryAccess)traceEntry;
                        instructionId = ((ulong)stackMemoryAccess.InstructionImageId << 32) | stackMemoryAccess.InstructionRelativeAddress;
                        memoryAddressId = stackMemoryAccess.MemoryRelativeAddress;

                        // Format instruction
                        StoreFormattedInstruction(instructionId,
                                                  traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[stackMemoryAccess.InstructionImageId],
                                                  stackMemoryAccess.InstructionRelativeAddress);
                        break;
                    }
                    default:
                        continue;
                }

                // Retrieve old hash
                if(!currentNode.InstructionHashes.TryGetValue(instructionId, out var hash))
                {
                    hash = new byte[16];
                    currentNode.InstructionHashes.Add(instructionId, hash);
                }

                // Update hash:
                // newHash = hash(oldHash || address)
                BinaryPrimitives.WriteUInt64LittleEndian(hash.AsSpan(8), memoryAddressId);
                BinaryPrimitives.WriteUInt64LittleEndian(hash.AsSpan(0), xxHash64.ComputeHash(hash, 16));
            }

            // Store call tree
            _testcaseCallTrees.AddOrUpdate(traceEntity.Id, rootNode, (t, n) => n);
        }

        public override async Task FinishAsync()
        {
            // Keep track of call stacks: Call stack ID -> [instruction ID 1, instruction ID 2, ...]
            // Note that the instruction order is reversed: Leaf, ..., Root
            var callStacks = new Dictionary<ulong, List<ulong>>();

            // Instruction data by (call stack ID, instruction ID)
            var instructions = new Dictionary<(ulong, ulong), InstructionData>();

            // Transform per-testcase call trees into flat per-(call stack, instruction) representation
            await Logger.LogInfoAsync("Transforming call trees");
            foreach(var testcase in _testcaseCallTrees)
            {
                // Iterate call tree using BFS
                Queue<CallTreeNode> pendingCallTreeNodes = new Queue<CallTreeNode>();
                pendingCallTreeNodes.Enqueue(testcase.Value);
                while(pendingCallTreeNodes.TryDequeue(out var currentNode))
                {
                    // If this call stack is still unknown, store it
                    if(!callStacks.ContainsKey(currentNode.CallStackId))
                    {
                        var currentParent = currentNode;
                        var callStack = new List<ulong>();
                        while(currentParent != null)
                        {
                            callStack.Add(currentParent.InstructionId);
                            currentParent = currentParent.Parent;
                        }

                        callStacks.Add(currentNode.CallStackId, callStack);
                    }

                    // Enqueue all children
                    foreach(var childNode in currentNode.Children)
                        pendingCallTreeNodes.Enqueue(childNode.Value);

                    // Iterate instructions at this call stack level
                    foreach(var instruction in currentNode.InstructionHashes)
                    {
                        // Did we already encounter this call stack? Retrieve instruction data container
                        if(!instructions.TryGetValue((currentNode.CallStackId, instruction.Key), out var instructionData))
                        {
                            instructionData = new InstructionData();
                            instructions.Add((currentNode.CallStackId, instruction.Key), instructionData);
                        }

                        // Add data from this testcase
                        ++instructionData.TestcaseCount;
                        instructionData.HashCounts.TryGetValue(instruction.Value, out int hashCount); // Will be 0 if not existing
                        instructionData.HashCounts[instruction.Value] = hashCount + 1;

                        // Store testcase IDs only when a full data dump is requested, since this is quite expensive
                        if(_dumpFullData)
                        {
                            // Make sure testcase ID list exists
                            if(!instructionData.HashTestcases.ContainsKey(instruction.Value))
                                instructionData.HashTestcases.Add(instruction.Value, new List<int>());
                            instructionData.HashTestcases[instruction.Value].Add(testcase.Key);
                        }
                    }
                }
            }

            // Instruction leakage by (call stack ID, instruction ID)
            var instructionLeakage = new Dictionary<(ulong, ulong), InstructionLeakageResult>();

            // Calculate leakage measures for each call stack/instruction tuple
            await Logger.LogInfoAsync("Running call stack memory access trace leakage analysis");
            double maximumMutualInformation = 0.0;
            foreach(var instruction in instructions)
            {
                var leakageResult = new InstructionLeakageResult();
                instructionLeakage.Add(instruction.Key, leakageResult);

                // Mutual information
                {
                    // Calculate probabilities of keys, and keys with traces (if they caused a call of this instruction)
                    // Since the keys are distinct and randomly generated, we have a uniform distribution
                    double pX = 1.0 / instruction.Value.TestcaseCount; // p(x)
                    double pXy = 1.0 / instruction.Value.TestcaseCount; // p(x,y)

                    // Calculate mutual information
                    double mutualInformation = 0.0;
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        double pY = (double)hashCount.Value / instruction.Value.TestcaseCount; // p(y)
                        mutualInformation += hashCount.Value * pXy * Math.Log2(pXy / (pX * pY));
                    }

                    leakageResult.MutualInformation = mutualInformation;
                }

                // Minimum entropy
                {
                    // Compute amount of unique traces
                    int uniqueTraceCount = instruction.Value.HashCounts.Count;
                    leakageResult.MinEntropy = Math.Log2(uniqueTraceCount);
                }

                // Conditional guessing entropy
                {
                    // Sum guessing entropy for each trace, weighting by its probability -> average value
                    double conditionalGuessingEntropy = 0.0;
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        // Probability of trace
                        double pY = (double)hashCount.Value / instruction.Value.TestcaseCount; // p(y)

                        // Sum over all possible inputs
                        // Application of Gaussian sum formula, simplification due to test cases being distinct and uniformly distributed -> p(x) = 1/n
                        conditionalGuessingEntropy += pY * (hashCount.Value + 1.0) / 2;
                    }

                    leakageResult.ConditionalGuessingEntropy = conditionalGuessingEntropy;
                }

                // Minimum conditional guessing entropy
                {
                    // Find minimum guessing entropy of each trace, weighting is not needed here
                    // Also store the hash value which has the lowest guessing entropy value
                    double minConditionalGuessingEntropy = double.MaxValue;
                    byte[] minConditionalGuessingEntropyHash = null;
                    foreach(var hashCount in instruction.Value.HashCounts)
                    {
                        double traceConditionalGuessingEntropy = (hashCount.Value + 1.0) / 2;
                        if(traceConditionalGuessingEntropy < minConditionalGuessingEntropy)
                        {
                            minConditionalGuessingEntropy = traceConditionalGuessingEntropy;
                            minConditionalGuessingEntropyHash = hashCount.Key;
                        }
                    }

                    leakageResult.MinConditionalGuessingEntropy = minConditionalGuessingEntropy;
                    leakageResult.MinConditionalGuessingEntropyHash = minConditionalGuessingEntropyHash;
                }
            }

            // Show warning if there likely were not enough testcases
            const double warnThreshold = 0.9;
            double testcaseCountBits = Math.Log2(_testcaseCallTrees.Count);
            if(maximumMutualInformation > testcaseCountBits - warnThreshold)
                await Logger.LogWarningAsync("For some instructions the calculated mutual information is suspiciously near to the testcase range. It is recommended to run more testcases.");

            // Store results
            await Logger.LogInfoAsync("Call stack memory access trace leakage analysis completed, writing results");
            string FormatCallStackId(ulong callStackId) => "CS-" + callStackId.ToString("X16");
            if(_outputFormat == OutputFormat.Txt)
            {
                // Store each measure in an own text file
                string basePath = _outputDirectory.FullName;
                await using var mutualInformationWriter = new StreamWriter(File.Create(Path.Combine(basePath, "mutual-information.txt")));
                await using var minEntropyWriter = new StreamWriter(File.Create(Path.Combine(basePath, "minimum-entropy.txt")));
                await using var condGuessEntropyWriter = new StreamWriter(File.Create(Path.Combine(basePath, "conditional-guessing-entropy.txt")));
                await using var minCondGuessEntropyWriter = new StreamWriter(File.Create(Path.Combine(basePath, "minimum-conditional-guessing-entropy.txt")));

                // Sort instructions by leakage in descending order
                var numberFormat = new NumberFormatInfo { NumberDecimalSeparator = ".", NumberDecimalDigits = 3 };
                foreach(var instructionData in instructionLeakage.OrderByDescending(l => l.Value.MutualInformation).ThenBy(mi => mi.Key))
                    await mutualInformationWriter.WriteLineAsync($"Instruction CS-{FormatCallStackId(instructionData.Key.Item1)}...{_formattedInstructions[instructionData.Key.Item2]}: " +
                                                                 $"{instructionData.Value.MutualInformation.ToString("N", numberFormat)} bits");
                foreach(var instructionData in instructionLeakage.OrderByDescending(l => l.Value.MinEntropy).ThenBy(mi => mi.Key))
                    await minEntropyWriter.WriteLineAsync($"Instruction CS-{FormatCallStackId(instructionData.Key.Item1)}...{_formattedInstructions[instructionData.Key.Item2]}: " +
                                                          $"{instructionData.Value.MinEntropy.ToString("N", numberFormat)} bits");
                foreach(var instructionData in instructionLeakage.OrderBy(l => l.Value.ConditionalGuessingEntropy).ThenBy(mi => mi.Key))
                    await condGuessEntropyWriter.WriteLineAsync($"Instruction CS-{FormatCallStackId(instructionData.Key.Item1)}...{_formattedInstructions[instructionData.Key.Item2]}: " +
                                                                $"{instructionData.Value.ConditionalGuessingEntropy.ToString("N", numberFormat)} guesses");
                foreach(var instructionData in instructionLeakage.OrderBy(l => l.Value.MinConditionalGuessingEntropy).ThenBy(mi => mi.Key))
                    await minCondGuessEntropyWriter.WriteLineAsync($"Instruction CS-{FormatCallStackId(instructionData.Key.Item1)}...{_formattedInstructions[instructionData.Key.Item2]}: " +
                                                                   $"{instructionData.Value.MinConditionalGuessingEntropy.ToString("N", numberFormat)} guesses " +
                                                                   $"[IN-{string.Concat(instructionData.Value.MinConditionalGuessingEntropyHash.Take(8).Select(b => b.ToString("X2")))}]");
            }
            else if(_outputFormat == OutputFormat.Csv)
            {
                // Write all measures into one CSV file
                await using var csvWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "memory-access-trace-leakage.csv")));

                // Header
                string listSeparator = ";"; // TextInfo.ListSeparator is unreliable
                await csvWriter.WriteLineAsync("Call Stack ID" +
                                               listSeparator +
                                               "Instruction" +
                                               listSeparator +
                                               "Mutual Information" +
                                               listSeparator +
                                               "Minimum Entropy" +
                                               listSeparator +
                                               "Conditional Guessing Entropy" +
                                               listSeparator +
                                               "Minimum Conditional Guessing Entropy" +
                                               listSeparator +
                                               "Minimum Conditional Guessing Entropy Hash");

                // Write leakage data
                foreach(var instructionData in instructionLeakage)
                {
                    var leakageData = instructionData.Value;
                    await csvWriter.WriteLineAsync(FormatCallStackId(instructionData.Key.Item1) +
                                                   listSeparator +
                                                   _formattedInstructions[instructionData.Key.Item2] +
                                                   listSeparator +
                                                   leakageData.MutualInformation.ToString("N3") +
                                                   listSeparator +
                                                   leakageData.MinEntropy.ToString("N3") +
                                                   listSeparator +
                                                   leakageData.ConditionalGuessingEntropy.ToString("N3") +
                                                   listSeparator +
                                                   leakageData.MinConditionalGuessingEntropy.ToString("N3") +
                                                   listSeparator +
                                                   "IN-" + string.Concat(leakageData.MinConditionalGuessingEntropyHash.Take(8).Select(b => b.ToString("X2"))));
                }
            }

            // Write call stacks
            await using var callStackWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-stacks.txt")));
            foreach(var callStack in callStacks)
            {
                await callStackWriter.WriteAsync($"CS-{callStack.Key:X16}: ");
                await WriteCallStackAsync(callStackWriter, ((IEnumerable<ulong>)callStack.Value).Reverse());
                await callStackWriter.WriteLineAsync();
            }
            
            // TODO make some use of hit counters?

            // Write entire state into file?
            if(_dumpFullData)
            {
                // Put everything into one big text file
                await using var fullDataWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "trace-hash-dump.txt")));

                // Structure:
                // callStack1
                //    instruction1:
                //       hash1: testcaseId1, testcaseId2, ...
                //       hash2: testcaseId3, ...
                //    instruction2
                // ...
                foreach(var callStack in instructions.GroupBy(ins => ins.Key.Item1))
                {
                    // Call stack name
                    await fullDataWriter.WriteAsync($"CS-{callStack.Key:X16}: ");
                    await WriteCallStackAsync(fullDataWriter, ((IEnumerable<ulong>)callStacks[callStack.Key]).Reverse());
                    await fullDataWriter.WriteLineAsync();

                    // Write instructions
                    foreach(var instruction in callStack)
                    {
                        // Instruction name
                        await fullDataWriter.WriteLineAsync("   " + _formattedInstructions[instruction.Key.Item2]);

                        // Hashes
                        foreach(var hashCount in instruction.Value.HashCounts)
                        {
                            // Write hash and number of hits
                            await fullDataWriter.WriteAsync($"      IN-{string.Concat(hashCount.Key.Take(8).Select(b => b.ToString("X2")))}: [{hashCount.Value}]");

                            // Write testcases yielding this hash
                            // Try to merge consecutive test case IDs: "1 3 4 5 7" -> "1 3-5 7"
                            int consecutiveStart = -1;
                            int consecutiveCurrent = -1;
                            const int consecutiveThreshold = 2;
                            foreach(var testcaseId in instruction.Value.HashTestcases[hashCount.Key])
                            {
                                if(consecutiveStart == -1)
                                {
                                    // Initialize first sequence
                                    consecutiveStart = testcaseId;
                                    consecutiveCurrent = testcaseId;
                                }
                                else if(testcaseId == consecutiveCurrent + 1)
                                {
                                    // We are in a sequence
                                    consecutiveCurrent = testcaseId;
                                }
                                else
                                {
                                    // We left the previous sequence
                                    // Did it reach the threshold? -> write it in the appropriate format
                                    if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
                                        await fullDataWriter.WriteAsync($" {consecutiveStart}-{consecutiveCurrent}");
                                    else
                                    {
                                        for(int t = consecutiveStart; t <= consecutiveCurrent; ++t)
                                            await fullDataWriter.WriteAsync($" {t}");
                                    }

                                    // New sequence
                                    consecutiveStart = testcaseId;
                                    consecutiveCurrent = testcaseId;
                                }
                            }

                            // Write remaining test case IDs of last sequence
                            if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
                                await fullDataWriter.WriteAsync($" {consecutiveStart}-{consecutiveCurrent}");
                            else
                            {
                                for(int t = consecutiveStart; t <= consecutiveCurrent; ++t)
                                    await fullDataWriter.WriteAsync($" {t}");
                            }

                            // End line
                            await fullDataWriter.WriteLineAsync();
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Utility function. Writes the given call stack in text format, as a single line without a line break at the end.
        /// </summary>
        /// <param name="writer">Stream writer.</param>
        /// <param name="callStack">Call stack, in desired order: Entry0 => Entry1 => ...</param>
        /// <returns></returns>
        private async Task WriteCallStackAsync(StreamWriter writer, IEnumerable<ulong> callStack)
        {
            bool first = true;
            foreach(var callStackEntry in callStack)
            {
                string callStackEntryFormatted = callStackEntry == 0 ? "<root>" : _formattedInstructions[callStackEntry];
                if(first)
                {
                    first = false;
                    await writer.WriteAsync(callStackEntryFormatted);
                    continue;
                }

                await writer.WriteAsync($" => {callStackEntryFormatted}");
            }
        }

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Extract output path
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath == null)
                throw new ConfigurationException("Missing output directory for analysis results.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Load MAP files
            var mapFilesNode = moduleOptions.GetChildNodeWithKey("map-files");
            if(mapFilesNode != null && mapFilesNode is YamlSequenceNode mapFileListNode)
                foreach(var mapFileNode in mapFileListNode.Children)
                    await _mapFileCollection.LoadMapFileAsync(mapFileNode.GetNodeString());

            // Check output format
            string outputFormat = moduleOptions.GetChildNodeWithKey("output-format")?.GetNodeString();
            if(outputFormat != null && !Enum.TryParse(outputFormat, true, out _outputFormat))
                throw new ConfigurationException("Invalid output format.");

            // Dump internal data?
            _dumpFullData = moduleOptions.GetChildNodeWithKey("dump-full-data")?.GetNodeBoolean() ?? false;
        }

        private void StoreFormattedInstruction(ulong instructionKey, TracePrefixFile.ImageFileInfo imageFileInfo, uint instructionAddress)
        {
            // Instruction already known?
            if(_formattedInstructions.ContainsKey(instructionKey))
                return;

            // Store formatted instruction
            _formattedInstructions.TryAdd(instructionKey, _mapFileCollection.FormatAddress(imageFileInfo, instructionAddress));
        }

        private class CallTreeNode
        {
            /// <summary>
            /// Identifier for the associated call instruction.
            /// </summary>
            public ulong InstructionId { get; set; }

            /// <summary>
            /// Identifier for the call stack until this node.
            /// Computed using a running hash: id = hash(parentId || instructionId).
            /// </summary>
            public ulong CallStackId { get; set; }

            /// <summary>
            /// Counts the number of times the call instruction of this node was hit.
            /// </summary>
            public int Hits { get; set; }

            /// <summary>
            /// Parent node.
            /// </summary>
            public CallTreeNode Parent { get; set; }

            /// <summary>
            /// Child nodes, indexed by target instruction ID.
            /// </summary>
            public Dictionary<ulong, CallTreeNode> Children { get; set; }

            /// <summary>
            /// Memory address hashes of read/write instructions. Instruction ID -> hash.
            /// </summary>
            public Dictionary<ulong, byte[]> InstructionHashes { get; set; }
        }

        /// <summary>
        /// Utility class to hold a tuple of testcase count/hash count.
        /// </summary>
        private class InstructionData
        {
            public int TestcaseCount { get; set; }
            public Dictionary<byte[], int> HashCounts { get; }

            /// <summary>
            /// This is only filled and used when a data dump is requested.
            /// </summary>
            public Dictionary<byte[], List<int>> HashTestcases { get; }

            public InstructionData()
            {
                TestcaseCount = 0;
                HashCounts = new Dictionary<byte[], int>(new ByteArrayComparer());
                HashTestcases = new Dictionary<byte[], List<int>>(new ByteArrayComparer());
            }
        }

        /// <summary>
        /// Stores leakage information for one instruction.
        /// </summary>
        private class InstructionLeakageResult
        {
            public double MutualInformation { get; set; }
            public double MinEntropy { get; set; }
            public double ConditionalGuessingEntropy { get; set; }
            public double MinConditionalGuessingEntropy { get; set; }
            public byte[] MinConditionalGuessingEntropyHash { get; set; }
        }

        /// <summary>
        /// Output formats.
        /// </summary>
        private enum OutputFormat
        {
            /// <summary>
            /// Output analysis results in text form (one file per measure).
            /// </summary>
            Txt,

            /// <summary>
            /// Output analysis in single CSV file (one column per measure).
            /// </summary>
            Csv
        }
    }
}