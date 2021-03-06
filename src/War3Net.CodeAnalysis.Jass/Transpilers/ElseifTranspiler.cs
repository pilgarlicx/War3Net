﻿// ------------------------------------------------------------------------------
// <copyright file="ElseifTranspiler.cs" company="Drake53">
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.
// </copyright>
// ------------------------------------------------------------------------------

#pragma warning disable SA1649 // File name should match first type name

using System;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace War3Net.CodeAnalysis.Jass.Transpilers
{
    public static partial class JassToCSharpTranspiler
    {
        public static StatementSyntax Transpile(this Syntax.ElseifSyntax elseifNode)
        {
            _ = elseifNode ?? throw new ArgumentNullException(nameof(elseifNode));

            var ifStatement = SyntaxFactory.IfStatement(
                elseifNode.ConditionExpressionNode.Transpile(),
                SyntaxFactory.Block(elseifNode.StatementListNode.Transpile()));

            return elseifNode.EmptyElseClauseNode is null
                ? ifStatement.WithElse(elseifNode.ElseClauseNode.Transpile())
                : ifStatement;
        }
    }
}