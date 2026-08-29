using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DfoGmTool.ServerCore.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    internal sealed class GmSystemMailResult
    {
        internal bool Success { get; set; }
        internal string Error { get; set; }
        internal long MessageId { get; set; }
        internal IReadOnlyList<long> MessageIds { get; set; } = Array.Empty<long>();
        internal int MessageCount { get; set; }
        internal int AttachmentCount { get; set; }
        internal bool Replayed { get; set; }
        // 整套发放时按部件能力回退过配置的物品; 前端据此提示"部分部件已按能力调整"。
        internal IReadOnlyList<int> AdjustedItemIds { get; set; } = Array.Empty<int>();

        internal static GmSystemMailResult Fail(string error)
            => new GmSystemMailResult { Success = false, Error = error };
    }

    internal sealed class GmSystemMailService
    {
        private const string SenderName = "DNFadmin";
        private const string ExpireAt = "9999-12-31 23:59:59";
        private const int AttachmentsPerMessage = 10;
        private const int MaximumMailMessages = 10;
        // 整套发放沿用上游的两封邮件上限, 与 PvfIndexService.SetSendMaxPieces 对应。
        private const int MaximumSetMailMessages = 2;
        private static readonly Regex RequestIdPattern = new Regex(
            "^[A-Za-z0-9:_-]{8,128}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly string _connectionString;
        private readonly NewInventoryStore _inventory;

        internal GmSystemMailService(string connectionString, NewInventoryStore inventory)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        }

        internal GmSystemMailResult SendItemGrant(
            int characterId,
            int expectedAccountId,
            int itemTemplateId,
            int count,
            ItemGrantOptions options,
            string requestId,
            string itemName)
        {
            requestId = (requestId ?? string.Empty).Trim();
            if (!RequestIdPattern.IsMatch(requestId))
                return GmSystemMailResult.Fail("发放请求编号无效，请刷新页面后重试");

            options ??= new ItemGrantOptions();
            var rootIdempotencyKey = "gm:" + requestId;
            var requestHash = ComputeRequestHash(
                characterId,
                expectedAccountId,
                itemTemplateId,
                count,
                options);

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);

                if (!TryLoadCharacter(
                        connection,
                        transaction,
                        characterId,
                        out var accountId,
                        out var characterName,
                        out var job))
                {
                    return GmSystemMailResult.Fail("角色不存在或已删除: " + characterId);
                }
                if (accountId != expectedAccountId)
                    return GmSystemMailResult.Fail("角色账号归属已变化，请刷新页面后重试");

                if (!_inventory.TryCreateMailAttachments(
                        job,
                        itemTemplateId,
                        count,
                        options,
                        out var attachments,
                        out var attachmentError))
                {
                    return GmSystemMailResult.Fail(attachmentError ?? "无法生成邮件附件");
                }

                var attachmentCount = attachments.Count;
                var messageCount = (attachmentCount + AttachmentsPerMessage - 1) / AttachmentsPerMessage;
                if (messageCount <= 0 || messageCount > MaximumMailMessages)
                {
                    return GmSystemMailResult.Fail(
                        "发放数量过大：每封邮件最多 " + AttachmentsPerMessage
                        + " 个附件、最多 " + MaximumMailMessages + " 封邮件（本次需要 "
                        + messageCount + " 封）");
                }

                var idempotencyKeys = BuildIdempotencyKeys(rootIdempotencyKey, messageCount);
                var replay = TryLoadReplaySet(
                    connection,
                    transaction,
                    characterId,
                    idempotencyKeys,
                    requestHash,
                    attachmentCount);
                if (replay != null)
                    return replay;

                var title = "GM物品发放";
                var body = string.IsNullOrWhiteSpace(itemName)
                    ? "GM 已向此角色发放物品，请在邮件中领取。"
                    : "GM 已发放「" + itemName.Trim() + "」，请在邮件中领取。";
                var messageIds = WriteShards(
                    connection,
                    transaction,
                    characterId,
                    accountId,
                    characterName,
                    title,
                    (part, total) => body,
                    attachments,
                    idempotencyKeys,
                    requestHash,
                    "GM tool item grant");

                transaction.Commit();
                return new GmSystemMailResult
                {
                    Success = true,
                    MessageId = messageIds[0],
                    MessageIds = messageIds,
                    MessageCount = messageIds.Count,
                    AttachmentCount = attachmentCount,
                    Replayed = false,
                };
            }
            catch (SqliteException ex)
            {
                return GmSystemMailResult.Fail("写入系统邮件失败: " + ex.Message);
            }
            catch (OverflowException ex)
            {
                return GmSystemMailResult.Fail("邮件物品参数超出服务端范围: " + ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       || ex is ArgumentException
                                       || ex is FormatException)
            {
                return GmSystemMailResult.Fail("生成系统邮件失败: " + ex.Message);
            }
        }

        // 整套发放: 每个部件固定 1 件, 附件按每封 10 件分片, 最多两封。
        // 与单件发放共用幂等 key/回放检测, 但 request_hash 走独立的输入格式, 避免与单件互相误判。
        internal GmSystemMailResult SendItemSetGrant(
            int characterId,
            int expectedAccountId,
            IReadOnlyList<int> memberIds,
            ItemGrantOptions options,
            string requestId,
            string setName,
            Func<int, string> resolveItemName)
        {
            requestId = (requestId ?? string.Empty).Trim();
            if (!RequestIdPattern.IsMatch(requestId))
                return GmSystemMailResult.Fail("发放请求编号无效，请刷新页面后重试");
            if (memberIds == null || memberIds.Count == 0)
                return GmSystemMailResult.Fail("套装部件为空");

            options ??= new ItemGrantOptions();
            var rootIdempotencyKey = "gm:" + requestId;
            var requestHash = ComputeSetRequestHash(characterId, expectedAccountId, memberIds, options);

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction(deferred: false);

                if (!TryLoadCharacter(
                        connection,
                        transaction,
                        characterId,
                        out var accountId,
                        out var characterName,
                        out var job))
                {
                    return GmSystemMailResult.Fail("角色不存在或已删除: " + characterId);
                }
                if (accountId != expectedAccountId)
                    return GmSystemMailResult.Fail("角色账号归属已变化，请刷新页面后重试");

                if (!TryCreateSetAttachments(
                        job,
                        memberIds,
                        options,
                        resolveItemName,
                        out var attachments,
                        out var adjustedItemIds,
                        out var attachmentError))
                {
                    return GmSystemMailResult.Fail(attachmentError);
                }

                return SendPreparedSet(
                    connection,
                    transaction,
                    characterId,
                    accountId,
                    characterName,
                    attachments,
                    adjustedItemIds,
                    rootIdempotencyKey,
                    requestHash,
                    setName);
            }
            catch (SqliteException ex)
            {
                return GmSystemMailResult.Fail("写入系统邮件失败: " + ex.Message);
            }
            catch (OverflowException ex)
            {
                return GmSystemMailResult.Fail("邮件物品参数超出服务端范围: " + ex.Message);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       || ex is ArgumentException
                                       || ex is FormatException)
            {
                return GmSystemMailResult.Fail("生成系统邮件失败: " + ex.Message);
            }
        }

        private GmSystemMailResult SendPreparedSet(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            string characterName,
            IReadOnlyList<GmMailAttachmentDraft> attachments,
            IReadOnlyList<int> adjustedItemIds,
            string rootIdempotencyKey,
            string requestHash,
            string setName)
        {
            var attachmentCount = attachments.Count;
            var messageCount = (attachmentCount + AttachmentsPerMessage - 1) / AttachmentsPerMessage;
            if (messageCount <= 0 || messageCount > MaximumSetMailMessages)
            {
                return GmSystemMailResult.Fail(
                    "套装部件超过两封邮件上限：每封最多 " + AttachmentsPerMessage
                    + " 个附件（本次需要 " + messageCount + " 封）");
            }

            var idempotencyKeys = BuildIdempotencyKeys(rootIdempotencyKey, messageCount);
            var replay = TryLoadReplaySet(
                connection,
                transaction,
                characterId,
                idempotencyKeys,
                requestHash,
                attachmentCount);
            if (replay != null)
                return replay;

            var display = string.IsNullOrWhiteSpace(setName) ? "套装" : setName.Trim();
            var messageIds = WriteShards(
                connection,
                transaction,
                characterId,
                accountId,
                characterName,
                "GM套装发放",
                (part, total) => total <= 1
                    ? "GM 已发放套装「" + display + "」，请在邮件中领取。"
                    : "GM 已发放套装「" + display + "」（" + part + "/" + total + "），请在邮件中领取。",
                attachments,
                idempotencyKeys,
                requestHash,
                "GM tool item set grant");

            transaction.Commit();
            return new GmSystemMailResult
            {
                Success = true,
                MessageId = messageIds[0],
                MessageIds = messageIds,
                MessageCount = messageIds.Count,
                AttachmentCount = attachmentCount,
                Replayed = false,
                AdjustedItemIds = adjustedItemIds ?? Array.Empty<int>(),
            };
        }

        private bool TryCreateSetAttachments(
            int job,
            IReadOnlyList<int> memberIds,
            ItemGrantOptions options,
            Func<int, string> resolveItemName,
            out IReadOnlyList<GmMailAttachmentDraft> attachments,
            out IReadOnlyList<int> adjustedItemIds,
            out string error)
        {
            attachments = Array.Empty<GmMailAttachmentDraft>();
            adjustedItemIds = Array.Empty<int>();
            error = null;

            var drafts = new List<GmMailAttachmentDraft>(memberIds.Count);
            var adjusted = new List<int>();
            foreach (var memberId in memberIds)
            {
                if (!TryCreateSetPieceAttachments(
                        job,
                        memberId,
                        options,
                        out var pieces,
                        out var wasAdjusted,
                        out var pieceError))
                {
                    var label = resolveItemName?.Invoke(memberId);
                    error = (string.IsNullOrWhiteSpace(label) ? "物品 " + memberId : label)
                        + ": " + (pieceError ?? "无法生成邮件附件");
                    return false;
                }

                if (wasAdjusted)
                    adjusted.Add(memberId);
                drafts.AddRange(pieces);
            }

            attachments = drafts;
            adjustedItemIds = adjusted;
            return true;
        }

        // 套装里各部件能力不同(戒指不能锻造、防具不能加红字、部分部件不限期),
        // 逐级回退到该部件支持的配置, 而不是让整套发放直接失败。
        private bool TryCreateSetPieceAttachments(
            int job,
            int itemTemplateId,
            ItemGrantOptions options,
            out IReadOnlyList<GmMailAttachmentDraft> attachments,
            out bool adjusted,
            out string error)
        {
            adjusted = false;
            if (_inventory.TryCreateMailAttachments(job, itemTemplateId, 1, options, out attachments, out error))
                return true;

            var firstError = error;
            foreach (var fallback in BuildSetPieceFallbacks(itemTemplateId, options))
            {
                if (_inventory.TryCreateMailAttachments(job, itemTemplateId, 1, fallback, out attachments, out error))
                {
                    adjusted = true;
                    return true;
                }
            }

            error = string.IsNullOrWhiteSpace(error) ? firstError : error;
            return false;
        }

        private static IEnumerable<ItemGrantOptions> BuildSetPieceFallbacks(
            int itemTemplateId,
            ItemGrantOptions options)
        {
            var trimmed = TrimToPieceCapability(itemTemplateId, options);
            if (trimmed != null)
                yield return trimmed;
            // 只保留品级: 丢掉强化/红字/锻造/期限/装扮属性
            yield return new ItemGrantOptions { QualityMode = options.QualityMode };
            yield return new ItemGrantOptions();
        }

        private static ItemGrantOptions TrimToPieceCapability(int itemTemplateId, ItemGrantOptions options)
        {
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata == null)
                return null;

            var capability = EquipmentGrantPolicy.Describe(metadata);
            if (!capability.IsEquipment)
                return null;

            // 强化等级在有红字时也能写入, 与 EquipmentGrantPolicy.TryApplyToBuilder 的判断保持一致
            var amplifyType = capability.CanAmplify ? options.AmplifyType : 0;
            return new ItemGrantOptions
            {
                QualityMode = options.QualityMode,
                UpgradeLevel = capability.CanUpgrade || amplifyType > 0 ? options.UpgradeLevel : 0,
                AmplifyType = amplifyType,
                ForgingLevel = capability.CanForge ? options.ForgingLevel : 0,
                AvatarOptionValue = options.AvatarOptionValue,
                ExpirationDays = options.ExpirationDays,
                ManualGrantType = options.ManualGrantType,
            };
        }

        // 分片写入: 每封最多 AttachmentsPerMessage 个附件, 附件 ordinal 按封重新编号。
        private static List<long> WriteShards(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            string characterName,
            string title,
            Func<int, int, string> bodyFactory,
            IReadOnlyList<GmMailAttachmentDraft> attachments,
            IReadOnlyList<string> idempotencyKeys,
            string requestHash,
            string auditReason)
        {
            var attachmentCount = attachments.Count;
            var messageCount = idempotencyKeys.Count;
            var messageIds = new List<long>(messageCount);
            for (var shard = 0; shard < messageCount; shard++)
            {
                var offset = shard * AttachmentsPerMessage;
                var shardCount = Math.Min(AttachmentsPerMessage, attachmentCount - offset);
                var shardKey = idempotencyKeys[shard];
                var messageId = InsertMessage(
                    connection,
                    transaction,
                    characterId,
                    accountId,
                    characterName,
                    title,
                    bodyFactory(shard + 1, messageCount),
                    shardKey,
                    requestHash);
                InsertRecipient(connection, transaction, messageId, characterId);

                for (var index = 0; index < shardCount; index++)
                {
                    var attachment = attachments[offset + index];
                    // mailbox_attachments.ordinal is scoped to one message;
                    // never leak the global draft ordinal into a shard.
                    attachment.Ordinal = index;
                    InsertAttachment(connection, transaction, messageId, attachment);
                }

                var auditId = InsertAudit(
                    connection,
                    transaction,
                    messageId,
                    accountId,
                    characterId,
                    characterName,
                    shardCount,
                    shardKey,
                    requestHash,
                    auditReason);
                for (var index = 0; index < shardCount; index++)
                    InsertAuditAttachment(connection, transaction, auditId, attachments[offset + index]);

                messageIds.Add(messageId);
            }

            return messageIds;
        }

        private static bool TryLoadCharacter(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out int accountId,
            out string characterName,
            out int job)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT account_id, name, job
FROM characters
WHERE character_id=@cid AND delete_flag=0
LIMIT 1;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                accountId = 0;
                characterName = string.Empty;
                job = 0;
                return false;
            }

            accountId = reader.GetInt32(0);
            characterName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            job = reader.GetInt32(2);
            return true;
        }

        private static List<string> BuildIdempotencyKeys(string rootKey, int messageCount)
        {
            var keys = new List<string>(messageCount);
            for (var index = 0; index < messageCount; index++)
            {
                // 保留旧版单封邮件使用的 gm:<requestId> key；多封时其余
                // 分片追加稳定的序号，重试时可以精确检测全有/全无。
                keys.Add(index == 0
                    ? rootKey
                    : rootKey + ":part:" + index.ToString(CultureInfo.InvariantCulture));
            }
            return keys;
        }

        private static GmSystemMailResult TryLoadReplaySet(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int senderCharacterId,
            IReadOnlyList<string> idempotencyKeys,
            string requestHash,
            int expectedAttachmentCount)
        {
            if (idempotencyKeys == null || idempotencyKeys.Count == 0)
                return null;

            var rootKey = idempotencyKeys[0];
            var expectedKeys = new HashSet<string>(idempotencyKeys, StringComparer.Ordinal);
            var replayRows = new Dictionary<string, ReplayMessage>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // Do not use LIKE here: request IDs intentionally allow '_',
                // and SQLite LIKE would treat it as a wildcard. Exact key
                // classification below also catches unexpected old shards.
                command.CommandText = @"
SELECT message_id, idempotency_key, request_hash,
       (SELECT COUNT(*) FROM mailbox_attachments a WHERE a.message_id=m.message_id)
FROM mailbox_messages m
WHERE sender_character_id=@cid AND idempotency_key IS NOT NULL
ORDER BY message_id;";
                command.Parameters.AddWithValue("@cid", senderCharacterId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var idempotencyKey = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (!IsRootOrShardKey(rootKey, idempotencyKey))
                        continue;
                    if (!expectedKeys.Contains(idempotencyKey))
                    {
                        return GmSystemMailResult.Fail(
                            "检测到同一请求的额外邮件分片，已拒绝回放；请使用新的请求编号重试");
                    }
                    if (replayRows.ContainsKey(idempotencyKey))
                    {
                        return GmSystemMailResult.Fail(
                            "检测到同一请求的重复邮件分片，已拒绝回放；请使用新的请求编号重试");
                    }

                    replayRows.Add(idempotencyKey, new ReplayMessage
                    {
                        MessageId = reader.GetInt64(0),
                        RequestHash = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        AttachmentCount = checked((int)reader.GetInt64(3)),
                    });
                }
            }

            var messageIds = new List<long>(idempotencyKeys.Count);
            var foundCount = 0;
            var storedAttachmentCount = 0;
            foreach (var idempotencyKey in idempotencyKeys)
            {
                if (!replayRows.TryGetValue(idempotencyKey, out var replayRow))
                    continue;

                foundCount++;
                if (!string.Equals(replayRow.RequestHash, requestHash, StringComparison.Ordinal))
                    return GmSystemMailResult.Fail("同一请求编号已用于不同的发放内容，请刷新页面后重试");

                messageIds.Add(replayRow.MessageId);
                storedAttachmentCount += replayRow.AttachmentCount;
            }

            if (foundCount == 0)
                return null;
            if (foundCount != idempotencyKeys.Count)
            {
                return GmSystemMailResult.Fail(
                    "检测到同一请求的部分邮件分片，已拒绝重发；请清理残留邮件后使用新的请求编号重试");
            }
            if (storedAttachmentCount != expectedAttachmentCount)
            {
                return GmSystemMailResult.Fail(
                    "检测到同一请求的邮件附件不完整，已拒绝重发；请检查邮箱后使用新的请求编号重试");
            }

            return new GmSystemMailResult
            {
                Success = true,
                MessageId = messageIds[0],
                MessageIds = messageIds,
                MessageCount = messageIds.Count,
                AttachmentCount = storedAttachmentCount,
                Replayed = true,
            };
        }

        private static bool IsRootOrShardKey(string rootKey, string candidate)
        {
            if (string.Equals(rootKey, candidate, StringComparison.Ordinal))
                return true;
            if (string.IsNullOrEmpty(candidate)
                || !candidate.StartsWith(rootKey + ":part:", StringComparison.Ordinal))
                return false;

            // Any exact suffix under this root is suspicious, including
            // malformed or non-canonical numbers (for example :part:01).
            return true;
        }

        private sealed class ReplayMessage
        {
            internal long MessageId { get; set; }
            internal string RequestHash { get; set; }
            internal int AttachmentCount { get; set; }
        }

        private static long InsertMessage(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            string characterName,
            string title,
            string body,
            string idempotencyKey,
            string requestHash)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO mailbox_messages (
    sender_character_id, sender_account_id, sender_name,
    receiver_character_id, receiver_account_id, receiver_name,
    title, body, gold, fee_gold, mail_type, source_protocol,
    idempotency_key, request_hash, unlimited_flag, expire_at
) VALUES (
    @cid, @aid, @senderName,
    @cid, @aid, @receiverName,
    @title, @body, 0, 0, 1, 0,
    @key, @hash, 1, @expireAt
);
SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@aid", accountId);
            command.Parameters.AddWithValue("@senderName", SenderName);
            command.Parameters.AddWithValue("@receiverName", characterName ?? string.Empty);
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@body", body);
            command.Parameters.AddWithValue("@key", idempotencyKey);
            command.Parameters.AddWithValue("@hash", requestHash);
            command.Parameters.AddWithValue("@expireAt", ExpireAt);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void InsertRecipient(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageId,
            int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO mailbox_recipients(message_id, character_id, folder)
VALUES(@messageId, @cid, 0);";
            command.Parameters.AddWithValue("@messageId", messageId);
            command.Parameters.AddWithValue("@cid", characterId);
            command.ExecuteNonQuery();
        }

        private static void InsertAttachment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageId,
            GmMailAttachmentDraft attachment)
        {
            if (attachment == null
                || attachment.ItemCoreData == null
                || attachment.ItemCoreData.Length != ItemCore.Size)
                throw new InvalidOperationException($"邮件附件 item_core 必须为 {ItemCore.Size}B");

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO mailbox_attachments (
    message_id, ordinal, item_type, source_list_type, source_slot_index, source_item_uid,
    item_template_id, item_kind, item_count, instance_value, durability, seal_flag,
    option_value, equipment_lock_id, expire_time, marker_16, pet_serial_or_handle,
    extra_json, item_core, detail_json
) VALUES (
    @messageId, @ordinal, @itemType, @sourceListType, 0, 0,
    @itemId, @itemKind, @count, @value, @durability, @seal,
    @option, 0, @expire, @marker, @pet,
    @extra, @core, @detail
);";
            command.Parameters.AddWithValue("@messageId", messageId);
            command.Parameters.AddWithValue("@ordinal", attachment.Ordinal);
            command.Parameters.AddWithValue("@itemType", (int)attachment.ItemType);
            command.Parameters.AddWithValue("@sourceListType", attachment.SourceListType);
            command.Parameters.AddWithValue("@itemId", attachment.ItemTemplateId);
            command.Parameters.AddWithValue("@itemKind", attachment.ItemKind ?? "unknown");
            command.Parameters.AddWithValue("@count", attachment.ItemCount);
            command.Parameters.AddWithValue("@value", attachment.InstanceValue);
            command.Parameters.AddWithValue("@durability", attachment.Durability);
            command.Parameters.AddWithValue("@seal", attachment.SealFlag);
            command.Parameters.AddWithValue("@option", attachment.OptionValue);
            command.Parameters.AddWithValue("@expire", attachment.ExpireTime);
            command.Parameters.AddWithValue("@marker", attachment.Marker16);
            command.Parameters.AddWithValue("@pet", attachment.PetSerialOrHandle);
            command.Parameters.AddWithValue("@extra", attachment.ExtraJson ?? "{}");
            command.Parameters.Add("@core", SqliteType.Blob).Value = attachment.ItemCoreData;
            command.Parameters.AddWithValue("@detail", attachment.DetailJson ?? string.Empty);
            command.ExecuteNonQuery();
        }

        private static long InsertAudit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageId,
            int accountId,
            int characterId,
            string characterName,
            int attachmentCount,
            string idempotencyKey,
            string requestHash,
            string auditReason)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO mailbox_system_mail_audit (
    message_id, actor_account_id, actor_character_id, actor_name, audit_reason,
    receiver_account_id, receiver_character_id, receiver_name,
    gold, attachment_count, mail_type, source_protocol,
    idempotency_key, request_hash, unlimited_flag, expire_at
) VALUES (
    @messageId, @aid, @cid, @actor, @reason,
    @aid, @cid, @receiverName,
    0, @attachmentCount, 1, 0,
    @key, @hash, 1, @expireAt
);
SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("@messageId", messageId);
            command.Parameters.AddWithValue("@aid", accountId);
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@actor", "dfo-gm-tool");
            command.Parameters.AddWithValue("@reason", auditReason ?? "GM tool item grant");
            command.Parameters.AddWithValue("@receiverName", characterName ?? string.Empty);
            command.Parameters.AddWithValue("@attachmentCount", attachmentCount);
            command.Parameters.AddWithValue("@key", idempotencyKey);
            command.Parameters.AddWithValue("@hash", requestHash);
            command.Parameters.AddWithValue("@expireAt", ExpireAt);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void InsertAuditAttachment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long auditId,
            GmMailAttachmentDraft attachment)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO mailbox_system_mail_audit_attachments (
    audit_id, ordinal, item_template_id, item_kind, item_count,
    instance_value, seal_flag, expire_time, pet_serial_or_handle, extra_json
) VALUES (
    @auditId, @ordinal, @itemId, @itemKind, @count,
    @value, @seal, @expire, @pet, @extra
);";
            command.Parameters.AddWithValue("@auditId", auditId);
            command.Parameters.AddWithValue("@ordinal", attachment.Ordinal);
            command.Parameters.AddWithValue("@itemId", attachment.ItemTemplateId);
            command.Parameters.AddWithValue("@itemKind", attachment.ItemKind ?? "unknown");
            command.Parameters.AddWithValue("@count", attachment.ItemCount);
            command.Parameters.AddWithValue("@value", attachment.InstanceValue);
            command.Parameters.AddWithValue("@seal", attachment.SealFlag);
            command.Parameters.AddWithValue("@expire", attachment.ExpireTime);
            command.Parameters.AddWithValue("@pet", attachment.PetSerialOrHandle);
            command.Parameters.AddWithValue("@extra", attachment.ExtraJson ?? "{}");
            command.ExecuteNonQuery();
        }

        private static string ComputeRequestHash(
            int characterId,
            int accountId,
            int itemTemplateId,
            int count,
            ItemGrantOptions options)
        {
            var fields = new List<string>
            {
                characterId.ToString(CultureInfo.InvariantCulture),
                accountId.ToString(CultureInfo.InvariantCulture),
                itemTemplateId.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture),
                ((int)options.QualityMode).ToString(CultureInfo.InvariantCulture),
                options.UpgradeLevel.ToString(CultureInfo.InvariantCulture),
                options.AmplifyType.ToString(CultureInfo.InvariantCulture),
                options.ForgingLevel.ToString(CultureInfo.InvariantCulture),
                options.AvatarOptionValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                options.ExpirationDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                (options.ManualGrantType ?? string.Empty).Trim().ToLowerInvariant(),
            };
            var builder = new StringBuilder(256);
            foreach (var field in fields)
                builder.Append(field.Length).Append(':').Append(field).Append('|');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        // 独立的输入格式("set|" 前缀 + 部件列表), 不改动单件发放的哈希输入,
        // 否则已经躺在邮箱里的旧请求会被判成"同一编号不同内容"。
        private static string ComputeSetRequestHash(
            int characterId,
            int accountId,
            IReadOnlyList<int> memberIds,
            ItemGrantOptions options)
        {
            var members = new List<string>(memberIds.Count);
            foreach (var memberId in memberIds)
                members.Add(memberId.ToString(CultureInfo.InvariantCulture));
            var fields = new List<string>
            {
                "set",
                characterId.ToString(CultureInfo.InvariantCulture),
                accountId.ToString(CultureInfo.InvariantCulture),
                string.Join(",", members),
                ((int)options.QualityMode).ToString(CultureInfo.InvariantCulture),
                options.UpgradeLevel.ToString(CultureInfo.InvariantCulture),
                options.AmplifyType.ToString(CultureInfo.InvariantCulture),
                options.ForgingLevel.ToString(CultureInfo.InvariantCulture),
                options.AvatarOptionValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                options.ExpirationDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                (options.ManualGrantType ?? string.Empty).Trim().ToLowerInvariant(),
            };
            var builder = new StringBuilder(256);
            foreach (var field in fields)
                builder.Append(field.Length).Append(':').Append(field).Append('|');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }
}
