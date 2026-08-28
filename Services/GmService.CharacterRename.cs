using System;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        public object RenameCharacter(int characterId, string newName)
        {
            var normalized = (newName ?? string.Empty).Trim();
            var invalidName = ValidateCharacterName(normalized);
            if (invalidName != null)
                return Error(invalidName);

            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    if (!ActiveCharacterExistsForRename(conn, tx, characterId))
                        return Error("角色不存在或已删除");

                    if (CharacterNameExists(conn, tx, normalized, characterId))
                        return Error("角色名已存在: " + normalized);

                    var nameBytes = GetClientCharacterNameBytes(normalized);
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
UPDATE characters
SET name = @nameBytes,
    name_bytes = @nameBytes,
    updated_at = @updatedAt
WHERE character_id = @characterId
  AND delete_flag = 0;";
                        cmd.Parameters.AddWithValue("@nameBytes", nameBytes);
                        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                        cmd.Parameters.AddWithValue("@characterId", characterId);
                        if (cmd.ExecuteNonQuery() != 1)
                            return Error("角色不存在或已删除");
                    }

                    tx.Commit();
                    return new { success = true, characterId, name = normalized };
                }
            }
        }

        private static bool ActiveCharacterExistsForRename(SqliteConnection conn, SqliteTransaction tx, int characterId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(1) FROM characters WHERE character_id = @characterId AND delete_flag = 0;";
                cmd.Parameters.AddWithValue("@characterId", characterId);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }

        private static bool CharacterNameExists(SqliteConnection conn, SqliteTransaction tx, string name, int excludedCharacterId)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
SELECT COUNT(1)
FROM characters
WHERE character_id <> @excludedCharacterId
  AND (name = @name OR name = @clientNameBytes OR name = @legacyUtf8NameBytes
       OR name_bytes = @clientNameBytes OR name_bytes = @legacyUtf8NameBytes);";
                cmd.Parameters.AddWithValue("@excludedCharacterId", excludedCharacterId);
                AddCharacterNameParameters(cmd, name);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            }
        }
    }
}
