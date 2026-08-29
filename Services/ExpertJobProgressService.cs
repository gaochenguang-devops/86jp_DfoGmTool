using System;
using System.Collections.Generic;
using DfoGmTool.ServerCore.Game.CharacterData;
using DfoGmTool.ServerCore.Game.SelectCharacter;
using DfoGmTool.ServerCore.Game.Skills;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    internal sealed class ExpertJobProgressSnapshot
    {
        public byte Type { get; set; }
        public string TypeName { get; set; }
        public uint Exp { get; set; }
        public int Level { get; set; }
        public int MaxLevel { get; set; }
        public uint MaxExp { get; set; }
        public uint CurrentLevelExp { get; set; }
        public int LearnedRecipeCount { get; set; }
        public int MachineGrade { get; set; }
        public int MachineEndurance { get; set; }
        public int MaxMachineGrade { get; set; }
        public List<object> Options { get; set; }
    }

    // 副职业类型/经验落 character_subtype0_fields, 机台与自动配方落 character_expert_job(_recipes)。
    // 送技不直改 character_skills: 走服务端同一套 CharacterSkillProfile.MergeGrants +
    // SkillStateService.ResolvePointState/ApplyProtocolMirrors, 再整页保存, 保证 SP 镜像不脱节。
    internal sealed class ExpertJobProgressService
    {
        private readonly string _connectionString;
        private readonly ExpertJobPvfData _pvfData;

        public ExpertJobProgressService(string connectionString, string pvfPath)
        {
            _connectionString = connectionString;
            _pvfData = new ExpertJobPvfData(pvfPath);
        }

        public bool TryLoad(int characterId, out ExpertJobProgressSnapshot snapshot, out string error)
        {
            snapshot = null;
            if (!TryLoadRecord(characterId, out var type, out var exp, out var recipes, out var grade, out var endurance, out error))
                return false;

            snapshot = BuildSnapshot(type, exp, recipes, grade, endurance);
            return true;
        }

        // 转职/觉醒覆写会按职业档案整页重建技能, 重建时必须把当前副职业的送技并回来,
        // 否则副职业技能会被顺带抹掉。PVF 里没有 .exj 时按“没有送技”处理, 不牵连转职流程。
        public IReadOnlyList<CharacterSkillProfile.SkillGrant> LoadActiveSkillGrants(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            try
            {
                var storedType = 0;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "SELECT COALESCE(expert_job_type, 0) FROM character_subtype0_fields WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    var value = command.ExecuteScalar();
                    if (value != null && value != DBNull.Value)
                        storedType = Convert.ToInt32(value);
                }

                if (storedType <= 0 || !_pvfData.TryGet(storedType, out var definition))
                    return Array.Empty<CharacterSkillProfile.SkillGrant>();

                var grants = new List<CharacterSkillProfile.SkillGrant>(definition.SkillGrants.Count);
                foreach (var grant in definition.SkillGrants)
                {
                    grants.Add(new CharacterSkillProfile.SkillGrant
                    {
                        SkillIndex = grant.SkillId,
                        Level = grant.Level,
                    });
                }

                return grants;
            }
            catch (Exception ex)
            {
                ServerCore.FileLogger.Log("[ExpertJobProgressService] load skill grants failed: " + ex.Message);
                return Array.Empty<CharacterSkillProfile.SkillGrant>();
            }
        }

        public bool TrySet(
            int characterId,
            int requestedType,
            int? requestedLevel,
            long? requestedExp,
            bool maxLevel,
            out ExpertJobProgressSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            if (requestedType < 0 || requestedType > byte.MaxValue)
            {
                error = "副职业类型无效。";
                return false;
            }

            ExpertJobDefinition definition = null;
            if (requestedType > 0 && !_pvfData.TryGet(requestedType, out definition))
            {
                error = "未知的副职业类型。";
                return false;
            }

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!TryLoadRecordInTransaction(
                            connection,
                            transaction,
                            characterId,
                            out var currentType,
                            out var currentExp,
                            out _,
                            out _,
                            out _,
                            out error))
                        return false;

                    var nextType = (byte)requestedType;
                    if (maxLevel && nextType == 0)
                    {
                        if (currentType == 0)
                        {
                            error = "请先选择副职业再一键满级。";
                            return false;
                        }

                        nextType = currentType;
                        if (!_pvfData.TryGet(nextType, out definition))
                        {
                            error = "未知的副职业类型。";
                            return false;
                        }
                    }

                    uint nextExp = 0;
                    if (nextType == 0)
                    {
                        nextExp = 0;
                    }
                    else if (definition == null)
                    {
                        error = "未知的副职业类型。";
                        return false;
                    }
                    else if (maxLevel)
                    {
                        nextExp = definition.MaxExp;
                    }
                    else if (requestedLevel.HasValue)
                    {
                        if (requestedLevel.Value < 1 || requestedLevel.Value > definition.MaxLevel)
                        {
                            error = "副职业等级范围 1-" + definition.MaxLevel + "。";
                            return false;
                        }

                        nextExp = definition.GetExpForLevel(requestedLevel.Value);
                    }
                    else if (requestedExp.HasValue)
                    {
                        if (requestedExp.Value < 0)
                        {
                            error = "副职业经验不能为负数。";
                            return false;
                        }

                        nextExp = requestedExp.Value > definition.MaxExp
                            ? definition.MaxExp
                            : (uint)requestedExp.Value;
                    }
                    else if (nextType == currentType)
                    {
                        nextExp = currentExp > definition.MaxExp ? definition.MaxExp : currentExp;
                    }

                    if (!WriteSubtype0(connection, transaction, characterId, nextType, nextExp))
                    {
                        error = "写入副职业类型/经验失败。";
                        return false;
                    }

                    WriteDomainState(
                        connection,
                        transaction,
                        characterId,
                        definition,
                        nextType,
                        nextExp,
                        typeChanged: nextType != currentType,
                        maxLevel);

                    if (nextType != currentType)
                    {
                        _pvfData.TryGet(currentType, out var previous);
                        if (!SyncExpertJobSkills(
                                connection,
                                transaction,
                                characterId,
                                previous != null ? previous.SkillGrants : null,
                                nextType > 0 && definition != null ? definition.SkillGrants : null))
                        {
                            error = "副职业技能同步失败。";
                            return false;
                        }
                    }

                    transaction.Commit();
                }
            }

            return TryLoad(characterId, out snapshot, out error);
        }

        private ExpertJobProgressSnapshot BuildSnapshot(
            byte type,
            uint exp,
            int recipes,
            int grade,
            int endurance)
        {
            _pvfData.TryGet(type, out var definition);
            var level = definition != null ? definition.GetLevel(exp) : 0;
            var maxLevel = definition != null ? definition.MaxLevel : 0;
            var maxExp = definition != null ? definition.MaxExp : 0u;
            var currentLevelExp = 0u;
            if (definition != null && level > 1)
                currentLevelExp = exp;

            var options = new List<object>
            {
                new { type = 0, name = "无副职业", maxLevel = 0, maxExp = 0u },
            };
            foreach (var job in _pvfData.All)
            {
                options.Add(new
                {
                    type = (int)job.Type,
                    name = job.Name,
                    maxLevel = job.MaxLevel,
                    maxExp = job.MaxExp,
                });
            }

            return new ExpertJobProgressSnapshot
            {
                Type = type,
                TypeName = definition != null ? definition.Name : "无副职业",
                Exp = type == 0 ? 0 : exp,
                Level = type == 0 ? 0 : level,
                MaxLevel = maxLevel,
                MaxExp = maxExp,
                CurrentLevelExp = currentLevelExp,
                LearnedRecipeCount = recipes,
                MachineGrade = grade,
                MachineEndurance = endurance,
                MaxMachineGrade = definition != null ? definition.MaxMachineGrade : 0,
                Options = options,
            };
        }

        private bool TryLoadRecord(
            int characterId,
            out byte type,
            out uint exp,
            out int recipes,
            out int grade,
            out int endurance,
            out string error)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return TryLoadRecordInTransaction(
                    connection,
                    null,
                    characterId,
                    out type,
                    out exp,
                    out recipes,
                    out grade,
                    out endurance,
                    out error);
            }
        }

        private static bool TryLoadRecordInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out byte type,
            out uint exp,
            out int recipes,
            out int grade,
            out int endurance,
            out string error)
        {
            type = 0;
            exp = 0;
            recipes = 0;
            grade = 0;
            endurance = 0;
            error = null;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT c.character_id,
       COALESCE(f.expert_job_type, 0),
       COALESCE(f.expert_job_exp, 0),
       COALESCE(e.disjoint_machine_grade, 0),
       COALESCE(e.disjoint_machine_endurance, 0),
       COALESCE(e.enchanter_endurance, 0)
FROM characters c
LEFT JOIN character_subtype0_fields f ON f.character_id = c.character_id
LEFT JOIN character_expert_job e ON e.character_id = c.character_id
WHERE c.character_id = @cid AND c.delete_flag = 0;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        error = "角色不存在: " + characterId;
                        return false;
                    }

                    var storedType = reader.GetInt32(1);
                    type = storedType > 0 && storedType <= byte.MaxValue ? (byte)storedType : (byte)0;
                    var storedExp = reader.GetInt64(2);
                    exp = storedExp < 0 ? 0 : storedExp > uint.MaxValue ? uint.MaxValue : (uint)storedExp;
                    grade = Math.Max(0, reader.GetInt32(3));
                    endurance = type == ExpertJobPvfData.EnchanterType
                        ? Math.Max(0, reader.GetInt32(5))
                        : Math.Max(0, reader.GetInt32(4));
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM character_expert_job_recipes WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                recipes = Convert.ToInt32(command.ExecuteScalar());
            }

            return true;
        }

        private static bool WriteSubtype0(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte type,
            uint exp)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_subtype0_fields (character_id, expert_job_type, expert_job_exp)
VALUES (@cid, @type, @exp)
ON CONFLICT(character_id) DO UPDATE SET
    expert_job_type=excluded.expert_job_type,
    expert_job_exp=excluded.expert_job_exp;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@type", (int)type);
                command.Parameters.AddWithValue("@exp", (long)exp);
                return command.ExecuteNonQuery() == 1;
            }
        }

        // 机台/耐久只在换职业、一键满级或清除副职业时重置, 平时保留玩家自己修出来的进度。
        private static void WriteDomainState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ExpertJobDefinition definition,
            byte type,
            uint exp,
            bool typeChanged,
            bool maxLevel)
        {
            var resetMachine = typeChanged || maxLevel || type == 0;
            var grade = 0;
            var disjointEndurance = 0;
            var enchanterEndurance = 0;
            if (type == ExpertJobPvfData.DisjointerType && definition != null)
            {
                grade = maxLevel
                    ? Math.Max(1, definition.MaxMachineGrade)
                    : Math.Max(1, definition.InitialMachineGrade);
                disjointEndurance = definition.GetEnduranceCap(grade);
            }
            else if (type == ExpertJobPvfData.EnchanterType && definition != null)
            {
                enchanterEndurance = maxLevel
                    ? definition.GetEnduranceCap(Math.Max(1, definition.MaxMachineGrade))
                    : definition.InitialEndurance;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, giveup_count,
    disjoint_machine_grade, disjoint_machine_endurance,
    enchanter_endurance, updated_at)
VALUES (@cid, 0, @grade, @endurance, @enchanterEndurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    disjoint_machine_grade=CASE WHEN @resetMachine=1 THEN excluded.disjoint_machine_grade ELSE character_expert_job.disjoint_machine_grade END,
    disjoint_machine_endurance=CASE WHEN @resetMachine=1 THEN excluded.disjoint_machine_endurance ELSE character_expert_job.disjoint_machine_endurance END,
    enchanter_endurance=CASE WHEN @resetMachine=1 THEN excluded.enchanter_endurance ELSE character_expert_job.enchanter_endurance END,
    updated_at=CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@grade", grade);
                command.Parameters.AddWithValue("@endurance", disjointEndurance);
                command.Parameters.AddWithValue("@enchanterEndurance", enchanterEndurance);
                command.Parameters.AddWithValue("@resetMachine", resetMachine ? 1 : 0);
                command.ExecuteNonQuery();
            }

            var expected = type > 0 && definition != null
                ? definition.GetAutoLearnRecipeIds(exp)
                : Array.Empty<int>();

            if (type == 0 || typeChanged)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM character_expert_job_recipes WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.ExecuteNonQuery();
                }
            }

            foreach (var recipeId in expected)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO character_expert_job_recipes (character_id, recipe_id)
VALUES (@cid, @recipe);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@recipe", recipeId);
                    command.ExecuteNonQuery();
                }
            }
        }

        // 换职业时先摘掉旧副职业送的技能条目, 再并入新副职业的送技, 最后按角色当前
        // job/level/附加点重算 SP/TP 并整页保存 —— 与转职/觉醒覆写走同一条路径,
        // 所以技能页头部的剩余点镜像不会脱节。
        private static bool SyncExpertJobSkills(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            IReadOnlyList<ExpertJobSkillGrant> removeGrants,
            IReadOnlyList<ExpertJobSkillGrant> addGrants)
        {
            var hasRemove = removeGrants != null && removeGrants.Count > 0;
            var hasAdd = addGrants != null && addGrants.Count > 0;
            if (!hasRemove && !hasAdd)
                return true;

            byte job;
            byte level;
            int growType;
            int bonusSp;
            int bonusTp;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT job, grow_type, level, bonus_sp, bonus_tp FROM characters WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;
                    job = (byte)Math.Max(0, Math.Min(byte.MaxValue, reader.GetInt32(0)));
                    growType = reader.GetInt32(1);
                    level = (byte)Math.Max(1, Math.Min(byte.MaxValue, reader.GetInt32(2)));
                    bonusSp = reader.GetInt32(3);
                    bonusTp = reader.GetInt32(4);
                }
            }

            // grow_type 低四位是转职段, 高四位是觉醒段, 与 SetGrowTypeFixed 的打包一致。
            var first = growType & 0xF;
            var second = (growType >> 4) & 0xF;

            var repository = SqliteCharacterProgressRepository.FromConnectionString(connection.ConnectionString);
            var skills = repository.LoadSkills(connection, transaction, characterId);
            if (hasRemove)
                RemoveGrantsFromSnapshot(skills, removeGrants);

            if (hasAdd)
            {
                var merged = new List<CharacterSkillProfile.SkillGrant>(addGrants.Count);
                foreach (var grant in addGrants)
                {
                    merged.Add(new CharacterSkillProfile.SkillGrant
                    {
                        SkillIndex = grant.SkillId,
                        Level = grant.Level,
                    });
                }

                CharacterSkillProfile.MergeGrants(skills, merged, job, level);
            }

            var points = SkillStateService.ResolvePointState(skills, job, level, bonusSp, bonusTp, first, second);
            SkillStateService.ApplyProtocolMirrors(skills, points);
            repository.SaveSkillProgress(connection, transaction, characterId, skills, points);
            return true;
        }

        private static void RemoveGrantsFromSnapshot(
            SkillInfoSnapshot skills,
            IReadOnlyList<ExpertJobSkillGrant> grants)
        {
            if (skills == null)
                return;

            var removed = new HashSet<ushort>();
            foreach (var grant in grants)
                removed.Add(grant.SkillId);

            foreach (var page in skills.Pages)
                page.Entries.RemoveAll(entry => removed.Contains(entry.SkillId));
        }
    }
}
