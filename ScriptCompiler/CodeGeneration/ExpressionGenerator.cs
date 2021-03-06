using System;
using System.Collections.Generic;
using System.Linq;
using ScriptCompiler.AST;
using ScriptCompiler.AST.Statements.Expressions;
using ScriptCompiler.AST.Statements.Expressions.Arithmetic;
using ScriptCompiler.CodeGeneration.Assembly;
using ScriptCompiler.CodeGeneration.Assembly.Instructions;
using ScriptCompiler.CompileUtil;
using ScriptCompiler.Types;
using ScriptCompiler.Visitors;
using Register = ScriptCompiler.CodeGeneration.Assembly.Register;

namespace ScriptCompiler.CodeGeneration {
    // The semantics of the ExpressionGenerator is to write the result of the expression onto the stack, and
    // bump the stack pointer accordingly.
    public class ExpressionGenerator : Visitor<List<Instruction>> {
        private readonly FunctionTypeRepository _functionTypeRepository;
        private readonly UserTypeRepository _userTypeRepository;
        private readonly Dictionary<string, StringLabel> _stringPoolAliases;
        private readonly RegisterManager _regManager;

        private StackFrame _stackFrame;

        private Register StackPointer => _regManager.StackPointer;

        private TypeIdentifier TypeIdentifier => new TypeIdentifier(
            _functionTypeRepository, _userTypeRepository, _stackFrame);

        private AddressabilityChecker AddressabilityChecker => new AddressabilityChecker(
            _functionTypeRepository, _userTypeRepository, _stackFrame);

        private LValueGenerator LValueGenerator => new LValueGenerator(
            _functionTypeRepository, _userTypeRepository, _stackFrame, _regManager);

        public ExpressionGenerator(FunctionTypeRepository functionTypeRepository, UserTypeRepository userTypeRepository,
                                   Dictionary<string, StringLabel> stringPoolAliases, StackFrame stackFrame,
                                   RegisterManager regManager) {
            _functionTypeRepository = functionTypeRepository;
            _userTypeRepository     = userTypeRepository;
            _stringPoolAliases      = stringPoolAliases;
            _stackFrame             = stackFrame;
            _regManager             = regManager;
        }

        public List<Instruction> Generate(ExpressionNode node) {
            return Visit(node as dynamic);
        }

        public override List<Instruction> Visit(ASTNode node) {
            throw new NotImplementedException(node.GetType().FullName);
        }

        public List<Instruction> Visit(VariableAccessNode node) {
            var instructions = new List<Instruction>();
            var (type, offset, guid) = _stackFrame.Lookup(node.Identifier);
            if (Equals(type, SType.SNoType)) {
                throw new CompileException($"Unknown variable {node.Identifier}", 0, 0);
            }

            // Copy the variable value onto the stack
            using var readLocation = _regManager.NewRegister();
            if (offset != null) {
                instructions.Add(
                    new MovInstruction(readLocation, StackPointer).WithComment($"Begin access {node.Identifier}"));
                instructions.Add(new AddInstruction(readLocation, offset));
            } else {
                instructions.Add(new MovInstruction(readLocation, new StaticLabel(guid.GetValueOrDefault()))
                                     .WithComment($"Begin static access {node.Identifier}"));
            }

            for (int i = 0; i < type.Length; i++) {
                // Use that register to copy across the object
                instructions.Add(new MemCopyInstruction(StackPointer, readLocation));
                instructions.Add(new AddInstruction(readLocation, 1));
                instructions.Add(PushStack(SType.SInteger));
            }

            return instructions;
        }

        public List<Instruction> Visit(FunctionCallNode node) {
            var instructions = new List<Instruction>();
            var returnType   = _functionTypeRepository.ReturnType(node.FunctionRef(TypeIdentifier));

            // ToList as we need deterministic iteration order
            var locals = _regManager.GetLocals().ToList();
            // Push local registers
            foreach (var register in locals) {
                instructions.Add(new MemWriteInstruction(StackPointer, register)
                                     .WithComment($"Back up local {register}"));
                instructions.Add(PushStack(SType.SInteger));
            }

            // Push space for the return value
            instructions.Add(PushStack(_functionTypeRepository.ReturnType(node.FunctionRef(TypeIdentifier))));

            // Push the parameters
            if (node.Function is StructAccessNode n) {
                // Method call; push 
                var (instructs, reg) = LValueGenerator.Generate(n.Left);
                instructions.AddRange(instructs);
                instructions.Add(new MemWriteInstruction(StackPointer, reg));
                instructions.Add(PushStack(SType.SGenericPtr));
            }
            foreach (var expressionNode in node.Args) {
                // Our contract with Generate is they will push the result onto the stack... exactly what we want!
                instructions.AddRange(Generate(expressionNode));
            }

            // Push the current instruction pointer
            var returnLabel = new Label($"return_{Guid.NewGuid()}");
            // Push the return address to the stack
            instructions.Add(new MemWriteInstruction(_regManager.StackPointer, returnLabel));
            // TODO: Check this logic? Make sure all stack operations make sense
            instructions.Add(new AddInstruction(_regManager.StackPointer, 1));
            instructions.Add(new JmpInstruction(new Label(node.AsmLabel(TypeIdentifier))));
            // The return address
            instructions.Add(new LabelInstruction(returnLabel));
            // Pop the arguments
            foreach (var expressionNode in node.Args) {
                // Our contract with Generate is they will push the result onto the stack... exactly what we want!
                instructions.Add(PopStack(TypeIdentifier.Identify(expressionNode)));
            }

            if (node.Function is StructAccessNode) {
                // Method call; pop this@ pointer 
                instructions.Add(PopStack(SType.SGenericPtr));
            }

            // If there are any locals...
            if (locals.Count > 0) {
                // Move down below return type
                instructions.Add(new SubInstruction(StackPointer, returnType.Length));
                // Pop local registers
                locals.Reverse();
                foreach (var register in locals) {
                    instructions.Add(PopStack(SType.SInteger));
                    instructions.Add(new MemReadInstruction(register, StackPointer));
                }

                // Now copy the return type
                using var copyReg = _regManager.NewRegister();
                instructions.Add(new MovInstruction(copyReg, StackPointer));
                instructions.Add(new AddInstruction(copyReg, locals.Count));
                for (int i = 0; i < returnType.Length; i++) {
                    instructions.Add(new MemCopyInstruction(StackPointer, copyReg));
                    instructions.Add(new AddInstruction(StackPointer, 1));
                    instructions.Add(new AddInstruction(copyReg, 1));
                }
            }

            // Conveniently, our contract is to put the result on the top of the stack... Where it already is :)
            return instructions;
        }

        public List<Instruction> Visit(IntegerLiteralNode node) {
            return new List<Instruction> {
                new MemWriteInstruction(StackPointer, (int) node.Value),
                PushStack(SType.SInteger)
            };
        }

        public List<Instruction> Visit(FloatLiteralNode node) {
            return new List<Instruction> {
                new MemWriteInstruction(StackPointer, BitConverter.SingleToInt32Bits(node.Value)),
                PushStack(SType.SFloat)
            };
        }

        public List<Instruction> Visit(SizeOfNode node) {
            // Special case for SizeOfNode: Allow type names
            int result;
            if (node.Arg is VariableAccessNode n && _userTypeRepository.ExistsType(n.Identifier)) {
                result = _userTypeRepository[n.Identifier].Length;
            } else {
                result = TypeIdentifier.Identify(node.Arg).Length;
            }

            return new List<Instruction> {
                new MemWriteInstruction(StackPointer, result),
                PushStack(SType.SInteger)
            };
        }

        public List<Instruction> Visit(StringLiteralNode node) {
            if (!_stringPoolAliases.ContainsKey(node.String)) {
                // TODO: Exception upgrade
                throw new ArgumentException();
            }

            return new List<Instruction> {
                new MemWriteInstruction(StackPointer, _stringPoolAliases[node.String]),
                PushStack(SType.SInteger)
            };
        }

        public List<Instruction> Visit(AssignmentNode node) {
            var nodeType = TypeIdentifier.Identify(node);
            var (instructions, pointerReg) = LValueGenerator.Generate(node.Destination);
            instructions[0].WithComment("Begin assignment");
            using (pointerReg) {
                instructions.AddRange(Generate(node.Value));
                instructions.Add(new SubInstruction(StackPointer, nodeType.Length));
                for (int i = 0; i < nodeType.Length; i++) {
                    instructions.Add(new MemCopyInstruction(pointerReg, StackPointer));
                    instructions.Add(new AddInstruction(pointerReg, 1));
                    instructions.Add(new AddInstruction(StackPointer, 1));
                }
            }

            return instructions;
        }

        public List<Instruction> Visit(StructAccessNode node) {
            var instructions = Generate(node.Left);
            var structType   = TypeIdentifier.Identify(node.Left);

            if (!(structType is UserType userStructType)) {
                throw new CompileException($"Unable to access field {node.Field} of non-struct type {structType}", 0,
                                           0);
            }

            // The entire struct is now on the stack
            instructions.Add(PopStack(userStructType));

            using var copyRegister = _regManager.NewRegister();

            instructions.Add(new MovInstruction(copyRegister, StackPointer));
            instructions.Add(new AddInstruction(copyRegister, userStructType.OffsetOfField(node.Field)));

            var typeOfField = userStructType.TypeOfField(node.Field);
            for (int i = 0; i < typeOfField.Length; i++) {
                instructions.Add(new MemCopyInstruction(StackPointer, copyRegister));
                instructions.Add(new AddInstruction(StackPointer, 1));
                instructions.Add(new AddInstruction(copyRegister, 1));
            }

            _stackFrame.Pushed(typeOfField);
            return instructions;
        }

        public List<Instruction> Visit(AddressOfNode node) {
            if (!AddressabilityChecker.Check(node.Expression)) {
                throw new CompileException("Attempt to take the address of an unaddressable expression", 0, 0);
            }

            var (instructions, reg) = LValueGenerator.Generate(node.Expression);
            using (reg) {
                instructions.Add(new MemWriteInstruction(StackPointer, reg));
                instructions.Add(PushStack(SType.SGenericPtr));
            }

            return instructions;
        }

        public List<Instruction> Visit(DereferenceNode node) {
            var pointerType   = TypeIdentifier.Identify(node.Expression);
            var referenceType = (pointerType as ReferenceType);
            if (referenceType == null) {
                throw new CompileException($"Unable to dereference expression of type {pointerType}", 0, 0);
            }

            var       instructions = Generate(node.Expression);
            using var copyReg      = _regManager.NewRegister();
            instructions.Add(PopStack(SType.SGenericPtr));
            instructions.Add(new MemReadInstruction(copyReg, StackPointer));
            var type = referenceType.ContainedType;
            for (int i = 0; i < type.Length; i++) {
                instructions.Add(new MemCopyInstruction(StackPointer, copyReg));
                instructions.Add(new AddInstruction(StackPointer, 1));
                instructions.Add(new AddInstruction(copyReg, 1));
            }

            _stackFrame.Pushed(type);

            return instructions;
        }

        public List<Instruction> Visit(EqualityOperatorNode node) {
            return VisitComparisonOperator(node, label => new JmpEqInstruction(label));
        }

        public List<Instruction> Visit(InequalityOperatorNode node) {
            return VisitComparisonOperator(node, label => new JmpNeqInstruction(label));
        }

        public List<Instruction> Visit(GreaterThanOperatorNode node) {
            return VisitComparisonOperator(node, label => new JmpGtInstruction(label));
        }

        public List<Instruction> Visit(LessThanOperatorNode node) {
            return VisitComparisonOperator(node, label => new JmpLtInstruction(label));
        }

        public List<Instruction> Visit(GreaterThanEqualOperatorNode node) {
            return VisitComparisonOperator(node, label => new JmpGteInstruction(label));
        }

        public List<Instruction> Visit(LessThanEqualOperatorNode node) {
            return VisitComparisonOperator(node, label => new JmpLteInstruction(label));
        }

        private List<Instruction> VisitComparisonOperator(BinaryOperatorNode node,
                                                          Func<Label, Instruction> operationGenerator) {
            var (instructions, left, right) = GenerateSingleWordBinOpSetup(node);
            instructions.Add(new CmpInstruction(left, right));
            var endLabel = new Label(Guid.NewGuid().ToString());
            var eqLabel  = new Label(Guid.NewGuid().ToString());
            instructions.Add(operationGenerator(eqLabel));
            // Neq case
            instructions.Add(new MemWriteInstruction(StackPointer, 0));
            instructions.Add(new JmpInstruction(endLabel));
            // Eq case
            instructions.Add(new LabelInstruction(eqLabel));
            instructions.Add(new MemWriteInstruction(StackPointer, 1));
            instructions.Add(new LabelInstruction(endLabel));
            instructions.Add(PushStack(SType.SInteger));
            left.Dispose();
            right.Dispose();
            return instructions;
        }

        public List<Instruction> Visit(AdditionNode node) {
            return VisitArithmeticOperator(node, (t, left, right) => new AddInstruction(left, right, t));
        }

        public List<Instruction> Visit(SubtractionNode node) {
            return VisitArithmeticOperator(node, (t, left, right) => new SubInstruction(left, right, t));
        }

        public List<Instruction> Visit(MultiplicationNode node) {
            return VisitArithmeticOperator(node, (t, left, right) => new MulInstruction(left, right, t));
        }

        public List<Instruction> Visit(DivisionNode node) {
            return VisitArithmeticOperator(node, (t, left, right) => new DivInstruction(left, right, t));
        }

        public List<Instruction> VisitArithmeticOperator(BinaryOperatorNode node,
                                                         Func<SType, Register, Register, Instruction>
                                                             operationGenerator) {
            var instructions = new List<Instruction>();
            var (opInstructions, left, right) = GenerateSingleWordBinOpSetup(node);
            instructions.AddRange(opInstructions);
            SType lType = TypeIdentifier.Identify(node.Left);
            instructions.Add(operationGenerator(lType, left, right));
            instructions.Add(new MemWriteInstruction(StackPointer, left));
            instructions.Add(PushStack(SType.SInteger));
            left.Dispose();
            right.Dispose();
            return instructions;
        }

        /// <summary>
        /// Generates the setup for a binary operator for a single word operation
        /// </summary>
        public (List<Instruction>, Register, Register) GenerateSingleWordBinOpSetup(BinaryOperatorNode node) {
            List<Instruction> instructions = new List<Instruction>();
            instructions.AddRange(Generate(node.Left));
            instructions.Add(PopStack(SType.SInteger));
            var leftRegister = _regManager.NewRegister();
            instructions.Add(new MemReadInstruction(leftRegister, StackPointer));
            instructions.AddRange(Generate(node.Right));
            instructions.Add(PopStack(SType.SInteger));
            var rightRegister = _regManager.NewRegister();
            instructions.Add(new MemReadInstruction(rightRegister, StackPointer));
            return (instructions, leftRegister, rightRegister);
        }

        private Instruction PushStack(SType type) {
            _stackFrame.Pushed(type);
            return new AddInstruction(StackPointer, type.Length);
        }

        private Instruction PopStack(SType type) {
            _stackFrame.Popped(type);
            return new SubInstruction(StackPointer, type.Length);
        }
    }
}
