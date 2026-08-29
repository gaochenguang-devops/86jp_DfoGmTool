namespace DfoGmTool.Services
{
    // 副职业面板: 类型/等级/经验覆写与一键满级。规则全部来自当前 PVF 的 .exj。
    public sealed partial class GmService
    {
        public object GetExpertJob(int characterId)
        {
            if (!_expertJob.TryLoad(characterId, out var snapshot, out var error))
                return Error(error);
            return ExpertJobResult(characterId, snapshot);
        }

        public object SetExpertJob(int characterId, int type, int? level, long? exp)
        {
            if (!_expertJob.TrySet(characterId, type, level, exp, maxLevel: false, out var snapshot, out var error))
                return Error(error);
            return ExpertJobResult(characterId, snapshot);
        }

        public object MaxExpertJob(int characterId, int? type)
        {
            if (!_expertJob.TrySet(
                    characterId,
                    type ?? 0,
                    requestedLevel: null,
                    requestedExp: null,
                    maxLevel: true,
                    out var snapshot,
                    out var error))
                return Error(error);
            return ExpertJobResult(characterId, snapshot);
        }

        private static object ExpertJobResult(int characterId, ExpertJobProgressSnapshot snapshot)
        {
            return new
            {
                success = true,
                characterId,
                type = snapshot.Type,
                typeName = snapshot.TypeName,
                exp = snapshot.Exp,
                level = snapshot.Level,
                maxLevel = snapshot.MaxLevel,
                maxExp = snapshot.MaxExp,
                learnedRecipeCount = snapshot.LearnedRecipeCount,
                machineGrade = snapshot.MachineGrade,
                machineEndurance = snapshot.MachineEndurance,
                maxMachineGrade = snapshot.MaxMachineGrade,
                options = snapshot.Options,
            };
        }
    }
}
