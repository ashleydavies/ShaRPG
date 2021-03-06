﻿using ScriptCompiler.CodeGeneration;

namespace ScriptCompiler.AST.Statements.Expressions.Arithmetic {
    public class AdditionNode : BinaryOperatorNode, IConstExpr {
        public AdditionNode(ExpressionNode left, ExpressionNode right) : base(left, right) {
            
        }

        public override uint[]? Calculate(CalcContext ctx) {
            return IConstExpr.CalcTwo(ctx, Left, Right, (uints, uints1) => {
                return new[]{ uints[0] + uints1[0] };
            });
        }
    }
}
