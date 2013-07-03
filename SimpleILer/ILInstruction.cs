using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SimpleILer
{
    /// <summary>
    /// ILバイト列中のIL命令にアクセスするための軽量オブジェクト
    /// </summary>
    public struct ILInstruction
    {
        private readonly byte[] _il;
        private readonly int _startIndex;
        private readonly short _opCodeValue;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="il"></param>
        /// <param name="startIndex"></param>
        /// <param name="opCodeValue"></param>
        public ILInstruction(byte[] il, int startIndex, short opCodeValue)
        {
            _il = il;
            _startIndex = startIndex;
            _opCodeValue = opCodeValue;
        }

        /// <summary>
        /// 命令のOpCode 構造体を取得します
        /// </summary>
        public OpCode OpCode
        {
            get { return _opCodes[_opCodeValue]; }
        }

        /// <summary>
        /// この命令のILバイト列中での開始インデックス
        /// </summary>
        public int StartIndex
        {
            get { return _startIndex; }
        }

        /// <summary>
        /// この命令を保持しているILバイト列
        /// </summary>
        public byte[] ILBytes
        {
            get { return _il; }
        }

        public int SizeWithOperand
        {
            get
            {
                var opCode = OpCode;
                switch (opCode.OperandType)
                {
                    case OperandType.ShortInlineR:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineString:
                    case OperandType.InlineSig:
                    case OperandType.InlineMethod:
                    case OperandType.InlineI:
                    case OperandType.InlineField:
                    case OperandType.InlineBrTarget:
                        return opCode.Size + 4;
                    case OperandType.InlineVar:
                        return opCode.Size + 2;
                    case OperandType.InlineR:
                    case OperandType.InlineI8:
                        return opCode.Size + 8;
                    case OperandType.InlineNone:
                        return opCode.Size;
                    case OperandType.InlineSwitch:
                        var count = _il.GetInt32(StartIndex + opCode.Size);
                        return opCode.Size + 4 + count*4;
                    case OperandType.ShortInlineVar:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineBrTarget:
                        return opCode.Size + 1;
                    default:
                        throw new NotSupportedException("unkown operand type");
                }
            }
        }

        // 参照する命令表
        private static readonly Dictionary<short, OpCode> _opCodes;
        static ILInstruction()
        {
            _opCodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
                                      .Where(f => f.FieldType == typeof(OpCode))
                                      .Select(f => (OpCode)f.GetValue(null))
                                      .Where(op => op.OpCodeType!= OpCodeType.Nternal)
                                      .ToDictionary( op=>op.Value);
        }

        public int GetBranchLocation()
        {
            var opCode = OpCode;
            var operandStart = _startIndex + opCode.Size;
            int branchOffset;
            int branchSource;
            switch (opCode.OperandType)
            {
                case OperandType.InlineBrTarget:
                    branchOffset = _il.GetInt32(operandStart);
                    branchSource = operandStart + 4;
                    break;
                case OperandType.ShortInlineBrTarget:
                    branchOffset = (sbyte) _il[operandStart];
                    branchSource = operandStart + 1;
                    break;
                default:
                    throw new NotSupportedException();
            }
            return branchSource + branchOffset;
        }

        public int[] GetBranchLocations()
        {
            var opCode = OpCode;
            Debug.Assert(opCode.OperandType == OperandType.InlineSwitch);

            var operandStart = _startIndex + opCode.Size;
            var switchCount = _il.GetInt32(operandStart);
            int sourceOffset = operandStart+4 + switchCount*4;
            var result = new int[switchCount];
            for (int i = 0, labelOffset =operandStart+4; i < switchCount; i++, labelOffset+=4)
            {
                result[i] = _il.GetInt32(labelOffset) + sourceOffset;
            }
            return result;
        }

        public int GetOperandToken()
        {
            var opCode = OpCode;
            var operandStart = _startIndex + opCode.Size;
            return _il.GetInt32(operandStart);
        }

        public int GetOperandVarIndex()
        {
            var opCode = OpCode;
            if (opCode.OperandType == OperandType.ShortInlineVar)
            {
                return _il[_startIndex + opCode.Size];
            }
            if(opCode.OperandType== OperandType.InlineVar)
            {
                return _il.GetInt16(_startIndex + opCode.Size);
            }
            throw new InvalidOperationException();
        }

        public int GetLocalIndex()
        {
            var opCode = OpCode;
            var opCodeValue = opCode.Value;
            int localIndex = 0;
            if (opCodeValue == OpCodes.Stloc_0.Value)
            {
                localIndex = 0;
            }
            else if (opCodeValue == OpCodes.Stloc_1.Value)
            {
                localIndex = 1;
            }
            else if (opCodeValue == OpCodes.Stloc_2.Value)
            {
                localIndex = 2;
            }
            else if (opCodeValue == OpCodes.Stloc_3.Value)
            {
                localIndex = 3;
            }
            else if (opCode.OperandType != OperandType.InlineNone)
            {
                localIndex = GetOperandVarIndex();
            }
            return localIndex;
        }

        public string ToString( Module module )
        {
            OpCode opCode = OpCode;
            string operandStr = "";
            int operandStart = _startIndex + opCode.Size;
            switch (opCode.OperandType)
            {
                case OperandType.InlineBrTarget:
                    break;
                case OperandType.InlineField:
                    operandStr = module.ResolveField(_il.GetInt32(operandStart)).Name;
                    break;
                case OperandType.InlineI:
                    operandStr = _il.GetInt32(operandStart).ToString();
                    break;
                case OperandType.InlineI8:
                    operandStr = _il.GetInt64(operandStart).ToString();
                    break;
                case OperandType.InlineMethod:
                    operandStr = module.ResolveMethod(_il.GetInt32(operandStart)).Name;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.InlineR:
                    operandStr = _il.GetDouble(operandStart).ToString();
                    break;
                case OperandType.InlineSig:
                    operandStr = string.Join("",
                                             module.ResolveSignature(_il.GetInt32(operandStart))
                                                   .Select(b => b.ToString("X2")));
                    break;
                case OperandType.InlineString:
                    operandStr = "\"" + module.ResolveString(_il.GetInt32(operandStart)) + "\"";
                    break;
                case OperandType.InlineSwitch:
                    break;
                case OperandType.InlineTok:
                    operandStr = module.ResolveType(_il.GetInt32(operandStart)).Name;
                    break;
                case OperandType.InlineType:
                    operandStr = module.ResolveType(_il.GetInt32(operandStart)).Name;
                    break;
                case OperandType.InlineVar:
                    operandStr = _il.GetInt16(operandStart).ToString();
                    break;
                case OperandType.ShortInlineBrTarget:
                    break;
                case OperandType.ShortInlineI:
                    operandStr = ((int) _il[operandStart]).ToString();
                    break;
                case OperandType.ShortInlineR:
                    operandStr = _il.GetSingle(operandStart).ToString();
                    break;
                case OperandType.ShortInlineVar:
                    operandStr = ((int) _il[operandStart]).ToString();
                    break;
            }
            return opCode.Name + " " + operandStr;
        }
    }
}