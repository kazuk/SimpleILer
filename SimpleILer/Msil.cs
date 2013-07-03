using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SimpleILer
{
    public class Msil
    {
        /// <summary>
        /// ILバイト列を命令毎に切り出し
        /// </summary>
        /// <param name="il"></param>
        /// <returns></returns>
        public static IEnumerable<ILInstruction> ILInstructions(byte[] il)
        {
            for (var index = 0; index < il.Length; )
            {
                var startIndex = index;

                var b = il[index];
                index++;
                OpCode opCode;
                if (!TryGetOpCode(b, out opCode))
                {
                    var b2 = il[index];
                    index++;
                    var s = (short)((b << 8) | b2);
                    if (!TryGetOpCode(s, out opCode))
                    {
                        throw new UnknownOpCodeException(il, startIndex);
                    }
                }

                yield return new ILInstruction(il, startIndex, opCode.Value);
                switch (opCode.OperandType)
                {
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineField:
                    case OperandType.InlineI:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    case OperandType.ShortInlineR:
                        index += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        index += 8;
                        break;
                    case OperandType.InlineNone:
                        break;
                    case OperandType.InlineSwitch:
                        int switchCount = il.GetInt32(index);
                        index += 4;
                        index += 4 * switchCount;
                        break;
                    case OperandType.InlineVar:
                        index += 2;
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        index++;
                        break;
                    default:
                        throw new NotSupportedException("unknown operand type");
                }
            }
        }

        /// <summary>
        /// 命令列を制御フロー単位で切り出し
        /// </summary>
        /// <param name="il"></param>
        /// <param name="clauses"></param>
        /// <returns></returns>
        public static IEnumerable<ILInstructionRun> GetRuns(byte[] il, ExceptionHandlingClause[] clauses)
        {
            var instructions = ILInstructions(il).ToArray();

            return GetRuns(instructions, il.Length, clauses);
        }

        /// <summary>
        /// 命令列を制御フロー単位で切り出し
        /// </summary>
        /// <param name="ilLength"></param>
        /// <param name="clauses"></param>
        /// <param name="instructions"></param>
        /// <returns></returns>
        public static IEnumerable<ILInstructionRun> GetRuns(ILInstruction[] instructions, int ilLength, ExceptionHandlingClause[] clauses)
        {
// 制御フローの飛び込み先オフセット
            var offsets = new List<int> {0}; // 最初のメソッド開始点にはデフォルトで飛び込んでくる

            var controlFrowSouceByTarget = new Dictionary<int, List<ControlFlowSource>>();
            Action<int, ControlFlowSource> appendSource = (target, source) =>
                {
                    List<ControlFlowSource> sourceList;
                    if (controlFrowSouceByTarget.TryGetValue(target, out sourceList))
                    {
                        sourceList.Add(source);
                    }
                    else
                    {
                        sourceList = new List<ControlFlowSource> {source};
                        controlFrowSouceByTarget.Add(target, sourceList);
                    }
                };

            // 例外ハンドリングでの飛び込みを要素追加
            appendSource(0, new ControlFlowSource {Type = ControlFlowSourceType.MethodEntry});
            for (int index = 0; index < clauses.Length; index++)
            {
                var clause = clauses[index];
                offsets.Add(clause.TryOffset);
                appendSource(clause.TryOffset,
                             new ControlFlowSource {Type = ControlFlowSourceType.BeginTry, ClauseIndex = index});
                offsets.Add(clause.HandlerOffset);
                appendSource(clause.HandlerOffset,
                             new ControlFlowSource {Type = ControlFlowSourceType.BeginHandler, ClauseIndex = index});

                if ((clause.Flags & ExceptionHandlingClauseOptions.Filter) != 0)
                {
                    offsets.Add(clause.FilterOffset);
                    appendSource(clause.HandlerOffset,
                                 new ControlFlowSource {Type = ControlFlowSourceType.BeginFilter, ClauseIndex = index});
                }
            }

            // IL 命令での制御フローを要素追加
            foreach (var inst in instructions)
            {
                var opCode = inst.OpCode;
                // switch 命令での分岐先
                if (opCode.Value == OpCodes.Switch.Value)
                {
                    var branchLocations = inst.GetBranchLocations();
                    offsets.AddRange(branchLocations);
                    foreach (var branchLocation in branchLocations)
                    {
                        Debug.Assert(instructions.Any(i => i.StartIndex == branchLocation));
                        appendSource(branchLocation,
                                     new ControlFlowSource
                                         {
                                             Type = ControlFlowSourceType.ILOperation,
                                             ILOffset = inst.StartIndex
                                         });
                    }
                }
                // それ以外での分岐先
                if (opCode.OperandType == OperandType.ShortInlineBrTarget || opCode.OperandType == OperandType.InlineBrTarget)
                {
                    var branchLocation = inst.GetBranchLocation();
                    Debug.Assert(instructions.Any(i => i.StartIndex == branchLocation));
                    offsets.Add(branchLocation);
                    appendSource(branchLocation,
                                 new ControlFlowSource {Type = ControlFlowSourceType.ILOperation, ILOffset = inst.StartIndex});
                }
                // 条件分岐での条件に当たらなかった側
                if (opCode.FlowControl == FlowControl.Cond_Branch)
                {
                    var nextInstOffset = inst.StartIndex + inst.SizeWithOperand;
                    Debug.Assert(instructions.Any(i => i.StartIndex == nextInstOffset));
                    offsets.Add(nextInstOffset);
                    appendSource(nextInstOffset,
                                 new ControlFlowSource
                                     {
                                         Type = ControlFlowSourceType.ConditionalNegative,
                                         ILOffset = inst.StartIndex
                                     });
                }
            }

            // 飛び込みオフセット毎に命令列を切り出す
            var runStarts = offsets.Distinct().OrderBy(n => n).ToArray();
            for (int i = 0; i < runStarts.Length; i++)
            {
                var runStart = runStarts[i];
                var nextRunStart = i + 1 == runStarts.Length ? ilLength : runStarts[i + 1];

                var ilInstructions =
                    instructions.Where(inst => runStart <= inst.StartIndex && inst.StartIndex < nextRunStart).ToArray();
                var lastInst = ilInstructions.Last();
                Debug.Assert(lastInst.StartIndex + lastInst.SizeWithOperand == nextRunStart);
                yield return
                    new ILInstructionRun(
                        ilInstructions,
                        controlFrowSouceByTarget[runStart],
                        runStart);
            }
        }

        private static readonly Dictionary<short, OpCode> _twoByteOpCode;
        private static readonly Dictionary<byte, OpCode> _oneByteOpCode;

        static Msil()
        {
            var opCodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(OpCode))
                .Select(f => (OpCode)f.GetValue(null))
                .Where(op => op.OpCodeType != OpCodeType.Nternal).ToArray();

            _twoByteOpCode = opCodes.Where(op => op.Size == 2).ToDictionary(op => op.Value);
            _oneByteOpCode = opCodes.Where(op => op.Size == 1).ToDictionary(op => (byte)op.Value);
        }

        private static bool TryGetOpCode(byte b, out OpCode opCode)
        {
            return _oneByteOpCode.TryGetValue(b, out opCode);
        }

        private static bool TryGetOpCode(short s, out OpCode opCode)
        {
            return _twoByteOpCode.TryGetValue(s, out opCode);
        }

        // TODO: FIXME 再設計 例外処理系 全般実装不足
        public static Dictionary<string, List<int>> PopulateControlFlowPath(byte[] il, ExceptionHandlingClause[] exceptionHandlingClauses)
        {
            var runs = GetRuns(il, exceptionHandlingClauses).ToArray();

            // Queue を介して進行解析する
            var currentRun = runs.Single(r => r.ControlFlowSources.Any(c => c.Type == ControlFlowSourceType.MethodEntry));
            var queueRuns = new Queue<Tuple<ILInstructionRun, List<int>>>();
            var controlFows = new Dictionary<string, List<int>>();

            queueRuns.Enqueue(Tuple.Create(currentRun, new List<int>()));
            while (queueRuns.Count != 0)
            {
                var qt = queueRuns.Dequeue();
                currentRun = qt.Item1;
                var path = new List<int>(qt.Item2) {currentRun.StartIndex};

                // 制御フローを分解して最後の命令に着目すればできる
                var lastInst = currentRun.Instructions.Last(); 

                OpCode opCode = lastInst.OpCode;
                // 強制ブランチなら次のフロー要素を解析させる
                if (opCode.FlowControl == FlowControl.Branch) 
                {
                    var branchLocation = lastInst.GetBranchLocation();

                    // TODO: FIXME Finally 内の分岐フロー追ってない
                    //  ちょっと乱暴すぎる実装、例外もやらないとダメ
                    //  Queue の型を直さなきゃいけなくてちょっと大変なので現状放置
                    var handler = exceptionHandlingClauses.Where(
                        clause => clause.Flags==ExceptionHandlingClauseOptions.Finally
                               && clause.TryOffset <= lastInst.StartIndex && lastInst.StartIndex < clause.TryOffset + clause.TryLength
                               && !(clause.TryOffset <= branchLocation && branchLocation < clause.TryOffset + clause.TryLength)).ToArray() ;
                    if (handler.Any()) 
                    {
                        path.Add( handler.First().HandlerOffset );
                    }

                    var nextRun = runs.Single(r => r.StartIndex == branchLocation);

                    if (path.Count(n => n == nextRun.StartIndex) <= 2)
                    {
                        queueRuns.Enqueue(Tuple.Create(nextRun, path));
                    }
                }
                    // 条件ブランチなら、ブランチ先と条件に当たらなかったパス双方に解析が必要
                else if (opCode.FlowControl == FlowControl.Cond_Branch) 
                {
                    ILInstructionRun nextRun;
                    if (opCode.Value == OpCodes.Switch.Value)
                    {
                        // switch の遷移先
                        var branchLocs = lastInst.GetBranchLocations().Distinct();
                        foreach (var branchLoc in branchLocs)
                        {
                            nextRun = runs.Single(r => r.StartIndex == branchLoc);
                            if (path.Count(n => n == nextRun.StartIndex) <= 2)
                            {
                                queueRuns.Enqueue(Tuple.Create(nextRun, path));
                            }
                        }
                    }
                    else
                    {
                        // それ以外の条件分岐の遷移先
                        nextRun = runs.Single(r => r.StartIndex == lastInst.GetBranchLocation());
                        if (path.Count(n => n == nextRun.StartIndex) <= 2)
                        {
                            queueRuns.Enqueue(Tuple.Create(nextRun, path));
                        }
                    }
                    // 条件分岐が発生しなかった場合の継続先
                    if (path.Count(n => n == lastInst.StartIndex + lastInst.SizeWithOperand) <= 2)
                    {
                        nextRun = runs.Single(r => r.StartIndex == lastInst.StartIndex + lastInst.SizeWithOperand);
                        queueRuns.Enqueue(Tuple.Create(nextRun, path));
                    }
                }
                    // リターンにたどり着いた、おめでとう
                else if (opCode.FlowControl == FlowControl.Return)
                {
                    var pathText = String.Join("=>", path.Select(o => String.Format("IL_{0:X4}", o)));
                    if (!controlFows.ContainsKey(pathText))
                    {
                        controlFows.Add(pathText, path);
                    }
                }
                    // それ以外の場合には続く制御フロー要素に実行が流れる
                else
                {
                    if (path.Count(n => n == lastInst.StartIndex + lastInst.SizeWithOperand) <= 2)
                    {
                        var nextRun = runs.Single(r => r.StartIndex == lastInst.StartIndex + lastInst.SizeWithOperand);
                        queueRuns.Enqueue(Tuple.Create(nextRun, path));
                    }
                }
            }
            return controlFows;
        }

        public static void SimulateEvalStack(IEnumerable<ILInstructionRun> runs, IEnumerable<int> controlFlow, Dictionary<int, List<Tuple<int, int>>> dataFlowSource, Module module)
        {
            var runArray = runs as ILInstructionRun[] ?? runs.ToArray();
            Action<int, int, int> appendSource = (target, popIndex, source) =>
                {
                    List<Tuple<int, int>> sources;
                    if (dataFlowSource.TryGetValue(target, out sources))
                    {
                        sources.Add(Tuple.Create(popIndex, source));
                    }
                    else
                    {
                        dataFlowSource.Add(target, new List<Tuple<int, int>> { Tuple.Create(popIndex, source) });
                    }
                };

            var stack = new Stack<int>();
            foreach (var flow in controlFlow)
            {
                var run = runArray.Single(r => r.StartIndex == flow);
                foreach (var inst in run.Instructions)
                {
                    OpCode opCode = inst.OpCode;
                    int popCount = 0;
                    int pushCount = 0;
                    if (opCode.StackBehaviourPop == StackBehaviour.Varpop ||
                        opCode.StackBehaviourPush == StackBehaviour.Varpush)
                    {
                        switch (opCode.OperandType)
                        {
                            case OperandType.InlineMethod:
                                var token = inst.GetOperandToken();
                                var method = module.ResolveMethod(token);
                                var para = method.GetParameters();
                                popCount = para.Length;
                                if (!method.IsConstructor && !method.IsStatic)
                                {
                                    popCount++;
                                }
                                if (!method.IsConstructor)
                                {
                                    var mi = (MethodInfo) method;
                                    pushCount = mi.ReturnType == typeof (void) ? 0 : 1;
                                }
                                break;
                            case OperandType.InlineNone:
                                break;
                            default:
                                throw new NotImplementedException();
                        }
                    }

                    switch (opCode.StackBehaviourPop)
                    {
                        default:
                            throw new NotSupportedException("んなもんしらねーよ");
                        case StackBehaviour.Pop0:
                            break;
                        case StackBehaviour.Pop1:
                        case StackBehaviour.Popi:
                        case StackBehaviour.Popref:
                            appendSource(inst.StartIndex,0, stack.Pop());
                            break;
                        case StackBehaviour.Pop1_pop1:
                        case StackBehaviour.Popi_pop1:
                        case StackBehaviour.Popi_popi:
                        case StackBehaviour.Popi_popi8:
                        case StackBehaviour.Popi_popr4:
                        case StackBehaviour.Popi_popr8:
                        case StackBehaviour.Popref_pop1:
                        case StackBehaviour.Popref_popi:
                            appendSource(inst.StartIndex,0, stack.Pop());
                            appendSource(inst.StartIndex,1, stack.Pop());
                            break;
                        case StackBehaviour.Popi_popi_popi:
                        case StackBehaviour.Popref_popi_pop1:
                        case StackBehaviour.Popref_popi_popi8:
                        case StackBehaviour.Popref_popi_popr4:
                        case StackBehaviour.Popref_popi_popr8:
                        case StackBehaviour.Popref_popi_popref:
                            appendSource(inst.StartIndex,0, stack.Pop());
                            appendSource(inst.StartIndex,1, stack.Pop());
                            appendSource(inst.StartIndex,2, stack.Pop());
                            break;
                        case StackBehaviour.Varpop:
                            for (int i = 0; i < popCount; i++)
                            {
                                appendSource(inst.StartIndex,i, stack.Pop());
                            }
                            break;
                    }

                    switch (opCode.StackBehaviourPush)
                    {
                        default:
                            throw new NotSupportedException("それも知らんて");
                        case StackBehaviour.Push0:
                            break;
                        case StackBehaviour.Push1:
                        case StackBehaviour.Pushi:
                        case StackBehaviour.Pushi8:
                        case StackBehaviour.Pushr4:
                        case StackBehaviour.Pushr8:
                        case StackBehaviour.Pushref:
                            stack.Push(inst.StartIndex);
                            break;
                        case StackBehaviour.Push1_push1:
                            stack.Push(inst.StartIndex);
                            stack.Push(inst.StartIndex);
                            break;
                        case StackBehaviour.Varpush:
                            for (int i = 0; i < pushCount; i++)
                            {
                                stack.Push(inst.StartIndex);
                            }
                            break;
                    }
                }
            }
        }
    }

    public enum ControlFlowSourceType
    {
        MethodEntry,
        BeginTry,
        BeginHandler,
        BeginFilter,
        ILOperation,
        ConditionalNegative
    }

    public class ControlFlowSource
    {
        public ControlFlowSourceType Type { get; set; }

        public int ClauseIndex { get; set; }

        public int ILOffset { get; set; }
    }

    public static class ByteArrayExtention
    {
        public static Int16 GetInt16(this byte[] il, int offset)
        {
            return BitConverter.ToInt16(il, offset);
        }

        public static Int32 GetInt32(this byte[] il, int offset)
        {
            return BitConverter.ToInt32(il, offset);
        }

        public static Int64 GetInt64(this byte[] il, int offset)
        {
            return BitConverter.ToInt64(il, offset);
        }

        public static Single GetSingle(this byte[] il, int offset)
        {
            return BitConverter.ToSingle(il, offset);
        }
        public static Double GetDouble(this byte[] il, int offset)
        {
            return BitConverter.ToDouble(il, offset);
        }

    }

    public class UnknownOpCodeException : Exception
    {
        private readonly byte[] _il;
        private readonly int _startIndex;

        public UnknownOpCodeException(byte[] il, int startIndex)
        {
            _il = il;
            _startIndex = startIndex;
        }

        public byte[] IL
        {
            get { return _il; }
        }

        public int StartIndex
        {
            get { return _startIndex; }
        }
    }
}
