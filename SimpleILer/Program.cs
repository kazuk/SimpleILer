using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SimpleILer
{

    class Program
    {

        // 基礎の基礎、IL 命令から分岐先を取得する
        static void Main(string[] args)
        {
            Action<string[]> self = (Main);

            var methodBody = self.Method.GetMethodBody();
            Debug.Assert(methodBody != null, "methodBody != null");
            var il = methodBody.GetILAsByteArray();
            foreach (var inst in Msil.ILInstructions(il))
            {
                var opCode = inst.OpCode;
                Console.WriteLine(
                        "IL_{0:X4}: {1}\t// {2} {3}", inst.StartIndex, opCode.Name, opCode.FlowControl, opCode.OperandType);

                var flowControl = opCode.FlowControl;
                if (flowControl == FlowControl.Next)
                {
                    continue;
                }

                int operandStartIndex = inst.StartIndex + opCode.Size;
                var operandType = opCode.OperandType;

                switch (flowControl)
                {
                    case FlowControl.Branch:
                        switch (operandType)
                        {
                            case OperandType.InlineBrTarget:
                                int inlineBrTarget = il.GetInt32(operandStartIndex);
                                int nextInstStart = operandStartIndex + 4 ; 
                                Console.WriteLine( " (InlineBrTarget){0:X4} IL_{1:X4}",inlineBrTarget, nextInstStart+inlineBrTarget);
                                break;
                            case OperandType.ShortInlineBrTarget:
                                var shortInlineBrTarget = (sbyte) il[operandStartIndex];
                                nextInstStart = operandStartIndex + 1;
                                Console.WriteLine(" (ShortInlineBrTarget){0:X2}  IL_{1:X4}", shortInlineBrTarget, nextInstStart + shortInlineBrTarget);
                                break;
                            default:
                                throw new NotImplementedException("ありえねー");
                        }
                        break;
                    case FlowControl.Cond_Branch:
                        switch (operandType)
                        {
                            case OperandType.InlineSwitch:
                                int switchCount = il.GetInt32(operandStartIndex);
                                int nextInstStart = operandStartIndex + 4 + switchCount; 
                                Console.WriteLine(" (switchCount){0}",switchCount);
                                for (var i = 0; i < switchCount; i++)
                                {
                                    int switchTarget = il.GetInt32(operandStartIndex + 4 + i*4);
                                    Console.WriteLine("  swithTarget[{0}] {1:X4} IL_{2:X4}",i,switchTarget, nextInstStart+switchTarget);
                                }
                                break;
                            case OperandType.InlineBrTarget:
                                var inlineBrTarget = il.GetInt32(operandStartIndex);
                                nextInstStart = operandStartIndex + 4 ; 
                                Console.WriteLine(" (InlineBrTarget) {0:X4} IL_{1:X4}", inlineBrTarget,nextInstStart+inlineBrTarget);
                                break;
                            case OperandType.ShortInlineBrTarget:
                                var shortInlineBrTarget = (sbyte) il[operandStartIndex];
                                nextInstStart = operandStartIndex + 1 ; 
                                Console.WriteLine(" (ShortInlineBrTarget){0:X2} IL_{1:X4}", shortInlineBrTarget, nextInstStart+shortInlineBrTarget);
                                break;
                            default:
                                throw new NotImplementedException("ありえねー");
                        }
                        break;
                    case FlowControl.Break:
                    case FlowControl.Call:
                    case FlowControl.Meta:
                    case FlowControl.Return:
                    case FlowControl.Throw:
                        break;
                }
            }

            AnalizeMore(methodBody, il, self);
        }

        private static void AnalizeMore(MethodBody methodBody, byte[] il, Action<string[]> self)
        {
            Console.ReadLine();

            // 分岐ターゲットが正しく取得できたので、分岐フローを元に構造認識が可能になります
            // 構造認識では、制御フローに関するステートメントに着目したいため、制御フロー影響
            // の無い命令群をまとめてそれらと制御フローの関連付けができる単位が必要になります。
            var exceptionHandlingClauses = methodBody.ExceptionHandlingClauses.ToArray();
            GettingControlFlowElement(il, exceptionHandlingClauses);

            Console.ReadLine();

            // 制御構造の認識ができると、IL上でプログラムがどのように進行するかという動作パスを
            // 考える事ができるようになります。動作パスを意識する事で、IL命令がどの順に実行され
            // えるのかが分かります。
            var controlFlow = Msil.PopulateControlFlowPath(il, exceptionHandlingClauses);
            Console.WriteLine(controlFlow.Count + " path found");
            Console.WriteLine(controlFlow.First().Key);

            Console.ReadLine();

            // 動作パスが列挙されたので、その動作パスを使ってILの評価スタックをシミュレートする
            // と、どのIL命令がどのIL命令の出力を使うのかというデータ依存関係が得られます。
            // このデータ依存関係は、最終的にローカル変数やフィールドに書かれる物がどのように計算
            // されるのか、条件分岐命令の条件はいったい何なのかを表現します。
            var ilinsts = Msil.ILInstructions(il).ToArray();
            var runs = Msil.GetRuns(ilinsts,il.Length, exceptionHandlingClauses).ToArray();
            var dataFlowSource = new Dictionary<int, List<Tuple<int, int>>>();
            foreach (var flow in controlFlow)
            {
                Msil.SimulateEvalStack(runs, flow.Value, dataFlowSource, self.Method.Module);
            }
            DisassembleWithDataFlow(runs, dataFlowSource);

            Console.ReadLine();

            // 依存関係グラフを再帰的に追う事でプログラム内のデータ依存関係を式風に表示する事も
            // できます。
            var setlocalOpCodes = new[]
                {
                    OpCodes.Stloc.Value,
                    OpCodes.Stloc_0.Value,
                    OpCodes.Stloc_1.Value,
                    OpCodes.Stloc_2.Value,
                    OpCodes.Stloc_3.Value,
                    OpCodes.Stloc_S.Value
                };

            foreach (var run in runs)
            {
                Console.WriteLine("IL_{0:X4}:",run.StartIndex);

                foreach (var instruction in run.Instructions)
                {
                    var opCode = instruction.OpCode;
                    if (setlocalOpCodes.Contains(opCode.Value))
                    {
                        var localIndex = instruction.GetLocalIndex();
                        var startIndex = instruction.StartIndex;
                        List<Tuple<int, int>> source;

                        string sourceDesc;
                        if (!dataFlowSource.TryGetValue(startIndex, out source))
                        {
                            sourceDesc = "/* unknown */"; // 例外による動作フローを追わない為、例外系での処理が認識されません
                        }
                        else
                        {
                            var popCnts = source.Select(t => t.Item1).Distinct().OrderBy(n => n);

                            var sources =
                                popCnts.Select(
                                    popCnt => string.Join(" or ", 
                                        source.Where(t => t.Item1 == popCnt)
                                            .Distinct()
                                            .Select(t => "(" + Describe(t.Item2, ilinsts, dataFlowSource,self.Method.Module) + ")"))).ToList();
                            sourceDesc = string.Join(",", sources);
                        }
                        Console.WriteLine("  local_{0} = {1}", localIndex, sourceDesc);
                    }

                    if (opCode.Value == OpCodes.Call.Value || opCode.Value == OpCodes.Calli.Value ||
                        opCode.Value == OpCodes.Callvirt.Value)
                    {
                        // 結果がどこにも使われない call は表示
                        if (dataFlowSource.Values.SelectMany(_ => _).All(t => t.Item2 != instruction.StartIndex))
                        {
                            Console.WriteLine("  {0}",
                                              Describe(instruction.StartIndex, ilinsts, dataFlowSource,
                                                       self.Method.Module));
                        }
                    }

                    if (opCode.FlowControl == FlowControl.Cond_Branch )
                    {
                        if (opCode.Value != OpCodes.Switch.Value)
                        {
                            Console.WriteLine("  {0} goto IL_{1:X4}",
                                              Describe(instruction.StartIndex, ilinsts, dataFlowSource,
                                                       self.Method.Module),
                                              instruction.GetBranchLocation());
                        }
                        else
                        {
                            Console.WriteLine("  {0} {1}",
                                              Describe(instruction.StartIndex, ilinsts, dataFlowSource,
                                                       self.Method.Module),
                                             string.Join(", ",instruction.GetBranchLocations().Select(n=> string.Format("IL_{0:X4}",n)))
                                );
                        }
                    }
                    if (opCode.FlowControl == FlowControl.Branch)
                    {
                        Console.WriteLine("  goto IL_{0:X4}", instruction.GetBranchLocation());
                    }
                }
            }
            Console.ReadLine();
        }

        /// <summary>
        /// 再帰的にデータ依存関係を追い、式っぽい文字列に組み立てます
        /// </summary>
        /// <param name="startIndex"></param>
        /// <param name="ilinsts"></param>
        /// <param name="dataFlowSource"></param>
        /// <returns></returns>
        private static string Describe(int startIndex, IEnumerable<ILInstruction> ilinsts, IReadOnlyDictionary<int, List<Tuple<int, int>>> dataFlowSource,Module module)
        {
            var instructions = ilinsts as ILInstruction[] ?? ilinsts.ToArray();
            var inst = instructions.Single(i => i.StartIndex == startIndex);
            List<Tuple<int, int>> source;
            if ( !dataFlowSource.TryGetValue(startIndex, out source))
            {
                return inst.ToString(module);
            }
            var popCnts = source.Select(t => t.Item1).Distinct().OrderByDescending(n => n);

            var sources = 
                popCnts.Select(popCnt => 
                               string.Join(" or ", source.Where(t => t.Item1 == popCnt).Distinct().Select(t =>  Describe(t.Item2, instructions, dataFlowSource,module) ))).ToList();
            return inst.ToString(module) + "(" + string.Join(",", sources) + ")";
        }

        private static void DisassembleWithDataFlow(IEnumerable<ILInstructionRun> runs, Dictionary<int, List<Tuple<int,int>>> dataFlowSource)
        {
            foreach (var run in runs)
            {
                foreach (var inst in run.Instructions )
                {
                    OpCode opCode = inst.OpCode;
                    Console.WriteLine( "IL_{0:X4}: {1}",inst.StartIndex, opCode.Name );
                    List<Tuple<int, int>> offset;
                    if (dataFlowSource.TryGetValue(inst.StartIndex, out offset))
                    {
                        foreach (var tuple in offset.GroupBy(t=>t.Item1))
                        {
                            Console.WriteLine("// popIndex#{0}", tuple.Key );
                            foreach (var source in tuple.Distinct())
                            {
                                Console.WriteLine("// using result of IL_{0:X4} ", source.Item2);
                            }
                            
                        }
                    }
                }
            }
        }

        // Msil.GetRuns が制御フローの要素分解をする
        private static void GettingControlFlowElement(byte[] il, ExceptionHandlingClause[] exceptionHandlingClauses)
        {
            foreach (var run in Msil.GetRuns(il, exceptionHandlingClauses))
            {
                Console.WriteLine("IL_{0:X4}:", run.StartIndex);
                foreach (var source in run.ControlFlowSources)
                {
                    var sourceDesc = source.Type == ControlFlowSourceType.ILOperation ||
                                     source.Type == ControlFlowSourceType.ConditionalNegative
                                         ? string.Format("IL_{0:X4}", source.ILOffset)
                                         : string.Format("exception block #{0}", source.ClauseIndex);
                    Console.WriteLine("\t// from {0} {1}", source.Type, sourceDesc);
                }
                foreach (var inst in run.Instructions)
                {
                    var opCode = inst.OpCode;
                    Console.WriteLine(
                        " {0}\t// {1} {2}", opCode.Name, opCode.FlowControl, opCode.OperandType);
                }
            }
        }
    }
}
