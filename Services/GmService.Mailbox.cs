using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        /// <summary>
        /// 删除当前角色收件箱(folder=0)中的收件人行。
        /// 共享邮件只有在没有任何 recipient 时才删除根消息；根消息删除
        /// 依赖 schema 外键级联附件，并让 campaign delivery 的 message_id
        /// 通过 ON DELETE SET NULL 保持可审计。
        /// </summary>
        public object ClearCharacterMailbox(int characterId)
        {
            if (characterId <= 0)
                return Error("角色编号无效");

            try
            {
                using var connection = new SqliteConnection(_config.ConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);

                if (!CharacterExists(connection, transaction, characterId))
                    return Error("角色不存在或已删除: " + characterId);

                var messageIds = LoadFolderMessages(connection, transaction, characterId);
                var counters = PurgeMailboxMessages(connection, transaction, characterId, messageIds);

                transaction.Commit();
                return new
                {
                    success = true,
                    characterId,
                    folder = 0,
                    recipientCount = counters.Recipients,
                    messageCount = counters.Messages,
                    attachmentCount = counters.Attachments,
                    auditCount = counters.Audits,
                    campaignReferenceCount = counters.CampaignReferences,
                };
            }
            catch (SqliteException ex)
            {
                return Error("清空邮箱失败: " + ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is OverflowException)
            {
                return Error("清空邮箱失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 删除单封邮件, 与「一键清空」同一套物理删除语义: 未领附件随根消息一并消失,
        /// 不会退回背包。共享邮件仍有其它收件人时只摘掉本角色的 recipient 行。
        /// </summary>
        public object DeleteMailboxMessage(int characterId, long messageId)
        {
            if (characterId <= 0)
                return Error("角色编号无效");
            if (messageId <= 0)
                return Error("邮件编号无效");

            try
            {
                using var connection = new SqliteConnection(_config.ConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);

                if (!CharacterExists(connection, transaction, characterId))
                    return Error("角色不存在或已删除: " + characterId);

                var counters = PurgeMailboxMessages(
                    connection,
                    transaction,
                    characterId,
                    new List<long> { messageId });
                if (counters.Recipients == 0)
                    return Error("邮件不存在或不属于该角色: " + messageId);

                transaction.Commit();
                return new
                {
                    success = true,
                    characterId,
                    messageId,
                    recipientCount = counters.Recipients,
                    messageCount = counters.Messages,
                    attachmentCount = counters.Attachments,
                    auditCount = counters.Audits,
                    campaignReferenceCount = counters.CampaignReferences,
                };
            }
            catch (SqliteException ex)
            {
                return Error("删除邮件失败: " + ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is OverflowException)
            {
                return Error("删除邮件失败: " + ex.Message);
            }
        }

        private sealed class MailboxPurgeCounters
        {
            public int Recipients;
            public int Messages;
            public int Attachments;
            public int Audits;
            public int CampaignReferences;
        }

        private static MailboxPurgeCounters PurgeMailboxMessages(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            IReadOnlyList<long> messageIds)
        {
            var counters = new MailboxPurgeCounters();
            foreach (var messageId in messageIds)
            {
                using (var deleteRecipient = connection.CreateCommand())
                {
                    deleteRecipient.Transaction = transaction;
                    deleteRecipient.CommandText = @"
DELETE FROM mailbox_recipients
WHERE message_id=@messageId AND character_id=@cid AND folder=0;";
                    deleteRecipient.Parameters.AddWithValue("@messageId", messageId);
                    deleteRecipient.Parameters.AddWithValue("@cid", characterId);
                    var removed = deleteRecipient.ExecuteNonQuery();
                    if (removed == 0)
                        continue;
                    counters.Recipients += removed;
                }

                // 其它角色/其它 folder 仍持有该消息时，只删除当前
                // recipient 行，根消息及其附件/审计都必须保留。
                if (CountMessageRecipients(connection, transaction, messageId) != 0)
                    continue;

                counters.Attachments += CountRows(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM mailbox_attachments WHERE message_id=@messageId;",
                    messageId);
                counters.Audits += CountRows(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE message_id=@messageId;",
                    messageId);
                counters.CampaignReferences += CountRows(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM mailbox_campaign_deliveries WHERE message_id=@messageId;",
                    messageId);

                // mailbox_system_mail_audit 没有指向 mailbox_messages 的
                // 外键，先显式清理审计及其附件，避免留下孤立记录。
                ExecuteDelete(
                    connection,
                    transaction,
                    "DELETE FROM mailbox_system_mail_audit_attachments WHERE audit_id IN (SELECT audit_id FROM mailbox_system_mail_audit WHERE message_id=@messageId);",
                    messageId);
                ExecuteDelete(
                    connection,
                    transaction,
                    "DELETE FROM mailbox_system_mail_audit WHERE message_id=@messageId;",
                    messageId);

                // 根消息删除由外键级联 mailbox_recipients/attachments；
                // mailbox_campaign_deliveries.message_id 会 SET NULL。
                ExecuteDelete(
                    connection,
                    transaction,
                    "DELETE FROM mailbox_messages WHERE message_id=@messageId;",
                    messageId);
                counters.Messages++;
            }

            return counters;
        }

        private static bool CharacterExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM characters WHERE character_id=@cid AND delete_flag=0;";
            command.Parameters.AddWithValue("@cid", characterId);
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }

        private static List<long> LoadFolderMessages(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var result = new List<long>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT message_id
FROM mailbox_recipients
WHERE character_id=@cid AND folder=0
ORDER BY message_id;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetInt64(0));
            return result;
        }

        private static int CountMessageRecipients(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageId)
        {
            return CountRows(
                connection,
                transaction,
                "SELECT COUNT(*) FROM mailbox_recipients WHERE message_id=@messageId;",
                messageId);
        }

        private static int CountRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            long messageId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@messageId", messageId);
            return checked((int)Convert.ToInt64(command.ExecuteScalar()));
        }

        private static void ExecuteDelete(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            long messageId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@messageId", messageId);
            command.ExecuteNonQuery();
        }

        // GM 列表不走服务端的 LoadInboxPage: 那条路径会顺手做过期清理, 而且只返回客户端
        // 当前那一页。这里直接列出该角色未删除的收件箱/保管邮件(含已过期)。
        public object ListMailbox(int characterId, PvfIndexService pvfIndex)
        {
            if (characterId <= 0)
                return Error("角色编号无效");
            if (!TryGetAccountId(characterId, out _))
                return Error("角色不存在: " + characterId);

            var rows = new List<MailboxRow>();
            var attachmentsByMessage = new Dictionary<long, List<MailboxAttachmentRow>>();
            using (var conn = new SqliteConnection(_config.ConnectionString))
            {
                conn.Open();
                LoadMailboxRows(conn, characterId, rows);
                if (rows.Count > 0)
                    LoadMailboxAttachments(conn, characterId, attachmentsByMessage);
            }

            var mails = new List<object>(rows.Count);
            foreach (var row in rows)
            {
                if (!attachmentsByMessage.TryGetValue(row.MessageId, out var attachments))
                    attachments = new List<MailboxAttachmentRow>();

                var attachmentViews = new List<object>(attachments.Count);
                foreach (var attachment in attachments)
                {
                    attachmentViews.Add(new
                    {
                        itemId = attachment.ItemTemplateId,
                        name = pvfIndex?.ResolveItemName(attachment.ItemTemplateId) ?? string.Empty,
                        rarity = pvfIndex?.ResolveItemRarity(attachment.ItemTemplateId) ?? 0,
                        count = attachment.ItemCount,
                        claimedFlag = attachment.ClaimedFlag,
                        claimed = attachment.ClaimedFlag != 0,
                    });
                }

                mails.Add(new
                {
                    messageId = row.MessageId,
                    senderCharacterId = row.SenderCharacterId,
                    senderName = row.SenderName,
                    title = row.Title,
                    body = row.Body,
                    gold = row.Gold,
                    goldClaimed = row.GoldClaimed,
                    mailType = row.MailType,
                    saved = row.Saved,
                    read = row.Read,
                    unlimited = row.Unlimited,
                    expireAt = row.ExpireAt,
                    createdAt = row.CreatedAt,
                    remainSeconds = row.RemainSeconds,
                    expired = row.Expired,
                    folder = row.Saved ? "保管" : "收件箱",
                    attachments = attachmentViews,
                });
            }

            return new { success = true, characterId, count = mails.Count, mails };
        }

        private static void LoadMailboxRows(
            SqliteConnection connection,
            int characterId,
            List<MailboxRow> rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    m.message_id,
    m.sender_character_id,
    m.sender_name,
    m.title,
    m.body,
    m.gold,
    m.mail_type,
    m.unlimited_flag,
    m.expire_at,
    m.created_at,
    r.read_flag,
    r.saved_flag,
    r.received_gold_flag,
    CASE
        WHEN m.unlimited_flag != 0 OR m.expire_at >= '9999-01-01 00:00:00' THEN 0
        ELSE MIN(
            2147483647,
            MAX(0, CAST(strftime('%s', m.expire_at) AS INTEGER) - CAST(strftime('%s', 'now') AS INTEGER)))
    END AS remain_seconds,
    CASE
        WHEN m.unlimited_flag != 0 OR m.expire_at >= '9999-01-01 00:00:00' THEN 0
        WHEN m.expire_at <= CURRENT_TIMESTAMP THEN 1
        ELSE 0
    END AS expired
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.folder = 0
  AND r.deleted_flag = 0
ORDER BY datetime(m.created_at) DESC, m.message_id DESC;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MailboxRow
                {
                    MessageId = reader.GetInt64(0),
                    SenderCharacterId = reader.GetInt32(1),
                    SenderName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Title = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Body = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    Gold = reader.GetInt32(5),
                    MailType = reader.GetInt32(6),
                    Unlimited = reader.GetInt32(7) != 0,
                    ExpireAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    CreatedAt = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    Read = reader.GetInt32(10) != 0,
                    Saved = reader.GetInt32(11) != 0,
                    GoldClaimed = reader.GetInt32(12) != 0,
                    RemainSeconds = reader.GetInt32(13),
                    Expired = reader.GetInt32(14) != 0,
                });
            }
        }

        private static void LoadMailboxAttachments(
            SqliteConnection connection,
            int characterId,
            Dictionary<long, List<MailboxAttachmentRow>> attachmentsByMessage)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT a.message_id, a.item_template_id, a.item_count, a.claimed_flag
FROM mailbox_attachments a
JOIN mailbox_recipients r ON r.message_id = a.message_id
WHERE r.character_id = @cid
  AND r.folder = 0
  AND r.deleted_flag = 0
ORDER BY a.message_id, a.ordinal, a.attachment_id;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var messageId = reader.GetInt64(0);
                if (!attachmentsByMessage.TryGetValue(messageId, out var attachments))
                {
                    attachments = new List<MailboxAttachmentRow>();
                    attachmentsByMessage.Add(messageId, attachments);
                }

                attachments.Add(new MailboxAttachmentRow
                {
                    ItemTemplateId = reader.GetInt32(1),
                    ItemCount = reader.GetInt32(2),
                    ClaimedFlag = reader.GetInt32(3),
                });
            }
        }

        private sealed class MailboxRow
        {
            public long MessageId;
            public int SenderCharacterId;
            public string SenderName;
            public string Title;
            public string Body;
            public int Gold;
            public int MailType;
            public bool Unlimited;
            public string ExpireAt;
            public string CreatedAt;
            public bool Read;
            public bool Saved;
            public bool GoldClaimed;
            public int RemainSeconds;
            public bool Expired;
        }

        private sealed class MailboxAttachmentRow
        {
            public int ItemTemplateId;
            public int ItemCount;
            public int ClaimedFlag;
        }
    }
}
