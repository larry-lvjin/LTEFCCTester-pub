namespace RFTestTool
{
    /// <summary>
    /// 控制 band 切换时是否需要 Cell OFF + CEST 重连。
    ///
    /// 规则：
    ///   1. SkipInit=true  AND  是第一个 band     → 直接测，不做任何初始化
    ///   2. SkipInit=true  AND  同模式（FDD→FDD / TDD→TDD） → 直接测
    ///   3. FDD → TDD 切换（无论 SkipInit）        → 必须 Cell OFF + CEST，且 MeasureSlot=2
    ///   4. TDD → FDD 切换                         → 不断开，直接切换参数
    ///   5. SkipInit=false                          → 每个 band 都做完整初始化
    /// </summary>
    public class BandSwitchHandler
    {
        public bool SkipInit { get; set; } = true;

        private string _prevMode = "";   // "FDD" or "TDD"
        private bool   _isFirst  = true;

        /// <summary>重置为初始状态（每次新测试任务开始时调用）</summary>
        public void Reset()
        {
            // 实现细节未公开（算法核心）
            throw new NotImplementedException("该方法的实现不在开源范围内");
        }

        public class SwitchDecision
        {
            /// <summary>是否需要执行 Cell OFF + wait CEST</summary>
            public bool NeedsCellOffCest { get; init; }
            /// <summary>是否需要将 MeasureSlot 设为 2（TDD 专用）</summary>
            public bool SetMeasureSlot2 { get; init; }
            /// <summary>日志说明</summary>
            public string Reason { get; init; } = "";
        }

        /// <summary>
        /// 在切换到新 band 前调用，返回本次切换应执行的操作。
        /// </summary>
        /// <param name="newMode">"FDD" 或 "TDD"</param>
        public SwitchDecision Decide(string newMode)
        {
            // 实现细节未公开（算法核心）
            throw new NotImplementedException("该方法的实现不在开源范围内");
        }
    }
}
