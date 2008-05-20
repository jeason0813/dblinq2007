﻿#region MIT license
// 
// Copyright (c) 2007-2008 Jiri Moudry
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// 
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using DbLinq.Factory;
using DbLinq.Linq.Data.Sugar.Pieces;

namespace DbLinq.Linq.Data.Sugar.Implementation
{
    public class PiecesDispatcher : IPiecesDispatcher
    {
        public IPiecesQueryService PiecesQueryService { get; set; }
        public PiecesService PiecesService { get; set; } // TODO: use interface when it's stable

        public PiecesDispatcher()
        {
            PiecesQueryService = ObjectFactory.Get<IPiecesQueryService>();
            PiecesService = ObjectFactory.Get<PiecesService>();
        }

        /// <summary>
        /// Entry point to analyze query related patterns.
        /// They start by a method, like Where(), Select()
        /// </summary>
        /// <param name="piece"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        public virtual Piece Dispatch(Piece piece, BuilderContext builderContext)
        {
            // our top-level entry parameter is the table
            // ... given the input type
            var entityPieceType = PiecesService.GetQueriedType(piece.Operands[2]);
            // ... we get the table
            var entityPiece = PiecesQueryService.RegisterTable(entityPieceType, builderContext);
            // and call the full method
            return Analyze(piece, new[] { entityPiece }, builderContext);
        }

        protected virtual Piece Analyze(Piece piece, BuilderContext builderContext)
        {
            return Analyze(piece, new Piece[0], builderContext);
        }

        protected virtual Piece Analyze(Piece piece, IList<Piece> parameters, BuilderContext builderContext)
        {
            // for constants, there's nothing we can do
            if (piece is ConstantPiece)
                return piece;

            var operationPiece = piece as OperationPiece;
            if (operationPiece != null)
            {
                switch (operationPiece.Operation)
                {
                case ExpressionType.Call:
                    return AnalyzeCall(PiecesService.GetMethodInfo(piece.Operands[0]).Name,
                                       PiecesService.MergeParameters(parameters,
                                       PiecesService.ExtractParameters(piece.Operands, 3)),
                                       builderContext);
                case ExpressionType.Lambda:
                    return AnalyzeLambda(piece, parameters, builderContext);
                case ExpressionType.Parameter:
                    return AnalyzeParameter(piece, builderContext);
                case ExpressionType.Quote:
                    return AnalyzeQuote(piece, parameters, builderContext);
                case ExpressionType.MemberAccess:
                    return AnalyzeMember(piece, builderContext);
                #region case ExpressionType.<Common operators>:
                case ExpressionType.Add:
                case ExpressionType.AddChecked:
                case ExpressionType.Divide:
                case ExpressionType.Modulo:
                case ExpressionType.Multiply:
                case ExpressionType.MultiplyChecked:
                case ExpressionType.Power:
                case ExpressionType.Subtract:
                case ExpressionType.SubtractChecked:
                case ExpressionType.And:
                case ExpressionType.Or:
                case ExpressionType.ExclusiveOr:
                case ExpressionType.LeftShift:
                case ExpressionType.RightShift:
                case ExpressionType.AndAlso:
                case ExpressionType.OrElse:
                case ExpressionType.Equal:
                case ExpressionType.NotEqual:
                case ExpressionType.GreaterThanOrEqual:
                case ExpressionType.GreaterThan:
                case ExpressionType.LessThan:
                case ExpressionType.LessThanOrEqual:
                case ExpressionType.Coalesce:
                //case ExpressionType.ArrayIndex
                //case ExpressionType.ArrayLength
                //case ExpressionType.Convert
                //case ExpressionType.ConvertChecked
                case ExpressionType.Negate:
                //case ExpressionType.NegateChecked
                case ExpressionType.Not:
                //case ExpressionType.TypeAs
                case ExpressionType.UnaryPlus:
                #endregion
                    return AnalyzeOperator(piece, builderContext);
                }
                throw Error.BadArgument(string.Format("S0052: Don't know what to do with expression {0}", piece));
            }
            if (parameters.Count != 0)
            {
                throw Error.BadArgument(
                    "S0088: There should be no parameter to a non-OperationPiece Piece (found {0} parameter(s))",
                    parameters.Count);
            }
            return piece;
        }

        protected virtual Piece AnalyzeCall(string methodName, IList<Piece> parameters, BuilderContext builderContext)
        {
            // all methods to handle are listed here:
            // ms-help://MS.VSCC.v90/MS.MSDNQTR.v90.en/fxref_system.core/html/2a54ce9d-76f2-81e2-95bb-59740c85386b.htm
            switch (methodName)
            {
            case "Select":
                return AnalyzeSelectQuery(parameters, builderContext);
            case "Where":
                return AnalyzeWhereQuery(parameters, builderContext);
            case "Average":
            case "Count":
            case "Max":
            case "Min":
            case "Sum":
                return AnalyzeProjectionQuery(methodName, builderContext);
            default:
                throw Error.BadArgument("S0133: Implement QueryMethod '{0}'", methodName);
            }
        }

        /// <summary>
        /// Returns a projection method call
        /// </summary>
        /// <param name="name"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Piece AnalyzeProjectionQuery(string name, BuilderContext builderContext)
        {
            return new OperationPiece(ExpressionType.Call,
                                      new ConstantPiece(name), // method name
                                      new ConstantPiece(null), // method object (null for static/extension methods)
                                      builderContext.PiecesQuery.Select); // we project on previous request (hope there is one)
        }

        /// <summary>
        /// Entry point for a Select()
        /// static Select(this Expression table, λ(table))
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Piece AnalyzeSelectQuery(IList<Piece> parameters, BuilderContext builderContext)
        {
            // just call back the underlying lambda (or quote, whatever)
            return Analyze(parameters[1], new[] { parameters[0] }, builderContext);
        }

        /// <summary>
        /// Entry point for a Where()
        /// static Where(this Expression table, λ(table))
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Piece AnalyzeWhereQuery(IList<Piece> parameters, BuilderContext builderContext)
        {
            var tablePiece = parameters[0];
            builderContext.PiecesQuery.Where.Add(Analyze(parameters[1], new[] { tablePiece }, builderContext));
            return tablePiece;
        }

        /// <summary>
        /// Handling a lambda consists in:
        /// - filling its input parameters with what's on the stack
        /// - using the body (parameters are registered in the context)
        /// </summary>
        /// <param name="piece"></param>
        /// <param name="parameters"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Piece AnalyzeLambda(Piece piece, IList<Piece> parameters, BuilderContext builderContext)
        {
            var lambdaExpression = (OperationPiece)piece;
            // for a lambda, first parameter is body, others are input parameters
            // so we create a parameters stack
            for (int parameterIndex = 1; parameterIndex < lambdaExpression.Operands.Count; parameterIndex++)
            {
                var parameter = PiecesService.GetParameterName(lambdaExpression.Operands[parameterIndex]);
                if (parameter == null)
                    throw Error.BadArgument("S0238: unknown argument type ({0})", lambdaExpression.Operands[parameterIndex]);
                builderContext.Parameters[parameter] = Analyze(parameters[parameterIndex - 1], builderContext);
            }
            // we keep only the body, the header is now useless
            // and once the parameters have been substituted, we don't pass one anymore
            return Analyze(lambdaExpression.Operands[0], builderContext);
        }

        /// <summary>
        /// When a parameter is used, we replace it with its original value
        /// </summary>
        /// <param name="piece"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Piece AnalyzeParameter(Piece piece, BuilderContext builderContext)
        {
            Piece unaliasedPiece;
            var parameterName = PiecesService.GetParameterName(piece);
            builderContext.Parameters.TryGetValue(parameterName, out unaliasedPiece);
            if (unaliasedPiece == null)
                throw Error.BadArgument("S0257: can not find parameter '{0}'", parameterName);
            return unaliasedPiece;
        }

        /// <summary>
        /// Analyzes a member access.
        /// This analyzis is down to top: the highest identifier is at bottom
        /// </summary>
        /// <param name="piece"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Piece AnalyzeMember(Piece piece, BuilderContext builderContext)
        {
            // first parameter is object, second is member
            var objectExpression = Analyze(piece.Operands[0], builderContext);
            var memberExpression = PiecesService.GetMemberInfo(piece.Operands[1]);
            // then see what we can do, depending on object type
            // - MetaTable --> then the result is a table
            // - Table --> the result may be a column or a join
            // - Object --> external parameter or table (can this happen here? probably not... to be checked)

            // if object is a table, then we need a column, or an association
            if (objectExpression.Is<TablePiece>())
            {
                var queryTableExpression = (TablePiece)objectExpression;
                // first of all, then, try to find the association
                var queryAssociationExpression = PiecesQueryService.RegisterAssociation(queryTableExpression, memberExpression,
                                                                                        builderContext);
                if (queryAssociationExpression != null)
                    return queryAssociationExpression;
                // then, try the column
                var queryColumnExpression = PiecesQueryService.RegisterColumn(queryTableExpression, memberExpression, builderContext);
                if (queryColumnExpression != null)
                    return queryColumnExpression;
                // then, cry
                throw Error.BadArgument("S0293: Column must be mapped. Non-mapped columns are not handled by now.");
            }

            // if object is still an object (== a constant), then we have an external parameter
            if (objectExpression.Is(ExpressionType.Constant))
            {
                var queryParameterExpression = PiecesQueryService.RegisterParameter(piece, builderContext);
                if (queryParameterExpression != null)
                    return queryParameterExpression;
                throw Error.BadArgument("S0302: Can not created parameter from expression '{0}'", piece);
            }

            throw Error.BadArgument("S0238: Don't know how to handle Piece");
        }

        /// <summary>
        /// A Quote creates a new local context, outside which created parameters disappear
        /// This is why we clone the BuilderContext
        /// </summary>
        /// <param name="piece"></param>
        /// <param name="parameters"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        public Piece AnalyzeQuote(Piece piece, IList<Piece> parameters, BuilderContext builderContext)
        {
            return Analyze(piece.Operands[0], parameters, builderContext.Clone());
        }

        /// <summary>
        /// Operator analysis consists in anlyzing all operands
        /// </summary>
        /// <param name="piece"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        public Piece AnalyzeOperator(Piece piece, BuilderContext builderContext)
        {
            for (int operandIndex = 0; operandIndex < piece.Operands.Count; operandIndex++)
            {
                var operand = piece.Operands[operandIndex];
                piece.Operands[operandIndex] = Analyze(operand, builderContext);
            }
            return piece;
        }
    }
}
