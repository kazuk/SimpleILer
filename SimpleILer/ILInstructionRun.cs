using System.Collections.Generic;
using System.Linq;

namespace SimpleILer
{
    /// <summary>
    /// 制御フロー命令での遷移単位での一連の命令列と、制御フローへの入り口を関連づけます
    /// </summary>
    public struct ILInstructionRun
    {
        private readonly IEnumerable<ControlFlowSource> _controlFlowSources;
        private readonly IEnumerable<ILInstruction> _instructions;
        private readonly int _startIndex;

        public ILInstructionRun(IEnumerable<ILInstruction> ilInstructions, IEnumerable<ControlFlowSource> controlFlowSources, int startIndex)
        {
            _startIndex = startIndex;
            _controlFlowSources = controlFlowSources.ToArray();
            _instructions = ilInstructions.ToArray();
        }

        public IEnumerable<ControlFlowSource> ControlFlowSources
        {
            get { return _controlFlowSources; }
        }

        public IEnumerable<ILInstruction> Instructions
        {
            get { return _instructions; }
        }

        public int StartIndex
        {
            get { return _startIndex; }
        }
    }
}