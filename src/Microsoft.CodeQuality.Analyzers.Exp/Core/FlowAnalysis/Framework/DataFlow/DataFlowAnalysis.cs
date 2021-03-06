﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.Operations.ControlFlow;

namespace Microsoft.CodeAnalysis.Operations.DataFlow
{
    /// <summary>
    /// Subtype for all dataflow analyses on a control flow graph.
    /// It performs a worklist based approach to flow abstract data values for <see cref="AnalysisEntity"/>/<see cref="IOperation"/> across the basic blocks until a fix point is reached.
    /// </summary>
    internal abstract class DataFlowAnalysis<TAnalysisData, TAnalysisResult, TAbstractAnalysisValue>
        where TAnalysisData : class
        where TAnalysisResult : AbstractBlockAnalysisResult<TAnalysisData, TAbstractAnalysisValue>
    {
        private static readonly ConditionalWeakTable<ControlFlowGraph, DataFlowAnalysisResult<TAnalysisResult, TAbstractAnalysisValue>> s_resultCache =
            new ConditionalWeakTable<ControlFlowGraph, DataFlowAnalysisResult<TAnalysisResult, TAbstractAnalysisValue>>();

        protected DataFlowAnalysis(AbstractAnalysisDomain<TAnalysisData> analysisDomain, DataFlowOperationVisitor<TAnalysisData, TAbstractAnalysisValue> operationVisitor)
        {
            AnalysisDomain = analysisDomain;
            OperationVisitor = operationVisitor;
        }

        protected AbstractAnalysisDomain<TAnalysisData> AnalysisDomain { get; }
        protected DataFlowOperationVisitor<TAnalysisData, TAbstractAnalysisValue> OperationVisitor { get; }
        private DataFlowAnalysisResult<NullAnalysis.NullBlockAnalysisResult, NullAnalysis.NullAbstractValue> NullAnalysisResultOpt { get; }

        protected DataFlowAnalysisResult<TAnalysisResult, TAbstractAnalysisValue> GetOrComputeResultCore(ControlFlowGraph cfg)
        {
            DataFlowAnalysisResult<TAnalysisResult, TAbstractAnalysisValue> result;
            if (!s_resultCache.TryGetValue(cfg, out result))
            {
                result = Run(cfg);
                s_resultCache.Add(cfg, result);
            }
            
            return result;
        }

        private DataFlowAnalysisResult<TAnalysisResult, TAbstractAnalysisValue> Run(ControlFlowGraph cfg)
        {
            var resultBuilder = new DataFlowAnalysisResultBuilder<TAnalysisData>();

            // Add each basic block to the result.
            foreach (var block in cfg.Blocks)
            {
                resultBuilder.Add(block);
            }

            var worklist = new Queue<BasicBlock>();
            var entry = GetEntry(cfg);

            // Initialize the input of the initial block
            // with the default abstract value of the domain.
            UpdateInput(resultBuilder, entry, AnalysisDomain.Bottom);

            // Add the entry block to the worklist.
            worklist.Enqueue(entry);

            while (worklist.Count > 0)
            {
                // Get the next block to process
                // and its associated result.
                var block = worklist.Dequeue();

                // Get the outputs of all predecessor blocks of the current block.
                var inputs = GetPredecessors(block).Select(b => GetOutput(resultBuilder[b]));

                // Merge all the outputs to get the new input of the current block.
                var input = AnalysisDomain.Merge(inputs);

                // Temporary workaround due to lack of *real* CFG
                // TODO: Remove the below if statement once we move to compiler's CFG
                // https://github.com/dotnet/roslyn-analyzers/issues/1567
                if (block.Kind == BasicBlockKind.Exit &&
                    OperationVisitor.MergedAnalysisDataAtReturnStatements != null)
                {
                    input = AnalysisDomain.Merge(input, OperationVisitor.MergedAnalysisDataAtReturnStatements);
                }
                
                // Compare the previous input with the new input.
                var compare = AnalysisDomain.Compare(GetInput(resultBuilder[block]), input);

                // The newly computed abstract values for each basic block
                // must be always greater or equal than the previous value
                // to ensure termination. 
                Debug.Assert(compare <= 0, "The newly computed abstract value must be greater or equal than the previous one.");

                // Is old input value < new input value ?
                if (compare < 0 || block.Kind == BasicBlockKind.Entry)
                {
                    // The newly computed value is greater than the previous value,
                    // so we need to update the current block result's
                    // input values with the new ones.
                    UpdateInput(resultBuilder, block, AnalysisDomain.Clone(input));
                    
                    // Flow the new input through the block to get a new output.
                    var output = Flow(block, input);

                    // Compare the previous output with the new output.
                    compare = AnalysisDomain.Compare(GetOutput(resultBuilder[block]), output);

                    // The newly computed abstract values for each basic block
                    // must be always greater or equal than the previous value
                    // to ensure termination. 
                    Debug.Assert(compare <= 0, "The newly computed abstract value must be greater or equal than the previous one.");

                    // Is old output value < new output value ?
                    if (compare < 0 || block.Kind == BasicBlockKind.Entry)
                    {
                        // The newly computed value is greater than the previous value,
                        // so we need to update the current block result's
                        // output values with the new ones.
                        UpdateOutput(resultBuilder, block, output);

                        // Since the new output value is different than the previous one, 
                        // we need to propagate it to all the successor blocks of the current block.
                        EnqueueRange(worklist, GetSuccessors(block));
                    }
                }
            }

            return resultBuilder.ToResult(ToResult, OperationVisitor.GetStateMap());
        }

        private TAnalysisData Flow(BasicBlock block, TAnalysisData data)
        {
            if (block.Kind == BasicBlockKind.Entry)
            {
                OperationVisitor.OnEntry(block, data);
            }
            else if (block.Kind == BasicBlockKind.Exit)
            {
                OperationVisitor.OnExit(block, data);
            }

            foreach (var statement in block.Statements)
            {
                data = OperationVisitor.Flow(statement, block, data);
            }

            return data;
        }

        private static void EnqueueRange<T>(Queue<T> self, IEnumerable<T> collection)
        {
            foreach (var item in collection)
            {
                if (!self.Contains(item))
                {
                    self.Enqueue(item);
                }
            }
        }

        internal abstract TAnalysisResult ToResult(BasicBlock basicBlock, DataFlowAnalysisInfo<TAnalysisData> blockAnalysisData);
        protected virtual BasicBlock GetEntry(ControlFlowGraph cfg) => cfg.Entry;
        protected virtual IEnumerable<BasicBlock> GetPredecessors(BasicBlock block) => block.Predecessors;
        protected virtual IEnumerable<BasicBlock> GetSuccessors(BasicBlock block) => block.Successors;
        protected virtual TAnalysisData GetInput(DataFlowAnalysisInfo<TAnalysisData> result) => result.Input;
        protected virtual TAnalysisData GetOutput(DataFlowAnalysisInfo<TAnalysisData> result) => result.Output;

        protected virtual void UpdateInput(DataFlowAnalysisResultBuilder<TAnalysisData> builder, BasicBlock block, TAnalysisData newInput)
        {
            var currentData = builder[block];
            var newData = currentData.WithInput(newInput);
            builder.Update(block, newData);
        }

        protected virtual void UpdateOutput(DataFlowAnalysisResultBuilder<TAnalysisData> builder, BasicBlock block, TAnalysisData newOutput)
        {
            var currentData = builder[block];
            var newData = currentData.WithOutput(newOutput);
            builder.Update(block, newData);
        }
    }
}