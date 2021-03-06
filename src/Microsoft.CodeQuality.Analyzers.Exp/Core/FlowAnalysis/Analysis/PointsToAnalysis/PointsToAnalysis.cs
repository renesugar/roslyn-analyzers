﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using Microsoft.CodeAnalysis.Operations.ControlFlow;

namespace Microsoft.CodeAnalysis.Operations.DataFlow.PointsToAnalysis
{
    using PointsToAnalysisData = IDictionary<AnalysisEntity, PointsToAbstractValue>;

    /// <summary>
    /// Dataflow analysis to track locations pointed to by <see cref="AnalysisEntity"/> and <see cref="IOperation"/> instances.
    /// </summary>
    internal partial class PointsToAnalysis : ForwardDataFlowAnalysis<PointsToAnalysisData, PointsToBlockAnalysisResult, PointsToAbstractValue>
    {
        private readonly DefaultPointsToValueGenerator _defaultPointsToValueGenerator;
        public static readonly AbstractValueDomain<PointsToAbstractValue> PointsToAbstractValueDomainInstance = PointsToAbstractValueDomain.Default;

        private PointsToAnalysis(PointsToAnalysisDomain analysisDomain, PointsToDataFlowOperationVisitor operationVisitor, DefaultPointsToValueGenerator defaultPointsToValueGenerator)
            : base(analysisDomain, operationVisitor)
        {
            _defaultPointsToValueGenerator = defaultPointsToValueGenerator;
        }

        public static DataFlowAnalysisResult<PointsToBlockAnalysisResult, PointsToAbstractValue> GetOrComputeResult(
            ControlFlowGraph cfg,
            ISymbol owningSymbol,
            DataFlowAnalysisResult<NullAnalysis.NullBlockAnalysisResult, NullAnalysis.NullAbstractValue> nullAnalysisResultOpt = null,
            bool pessimisticAnalysis = true)
        {
            var defaultPointsToValueGenerator = new DefaultPointsToValueGenerator();
            var analysisDomain = new PointsToAnalysisDomain(defaultPointsToValueGenerator, PointsToAbstractValueDomain.Default);
            var operationVisitor = new PointsToDataFlowOperationVisitor(defaultPointsToValueGenerator, analysisDomain, PointsToAbstractValueDomain.Default, owningSymbol, pessimisticAnalysis, nullAnalysisResultOpt);
            var pointsToAnalysis = new PointsToAnalysis(analysisDomain, operationVisitor, defaultPointsToValueGenerator);
            return pointsToAnalysis.GetOrComputeResultCore(cfg);
        }

        internal override PointsToBlockAnalysisResult ToResult(BasicBlock basicBlock, DataFlowAnalysisInfo<PointsToAnalysisData> blockAnalysisData) => new PointsToBlockAnalysisResult(basicBlock, blockAnalysisData, _defaultPointsToValueGenerator.GetDefaultPointsToValueMap());
    }
}
