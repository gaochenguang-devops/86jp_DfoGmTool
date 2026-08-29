// ---- 角色邮箱 ----
// 列表走 /api/characters/{id}/mailbox(GM 专用: 不做过期清理, 一次给全部收件箱/保管邮件),
// 删除与清空是同一套物理删除语义: 未领附件随邮件消失, 不会退回背包。

// 附件名按品级着色 + 图标/悬浮预览, 与背包/发放页共用同一套渲染
function mailboxItemName(itemId, name, rarity) {
  return itemPreviewName(itemId, name, rarity);
}

function mailboxSubject(mail) {
  const title = String(mail.title || '').trim();
  if (title) return title;
  return String(mail.body || '').trim() || '(无标题)';
}

function mailboxClaimLabel(flag) {
  if (flag === 2) return '领取中';
  return flag ? '已领' : '未领';
}

function mailboxGoldLabel(mail) {
  if (!mail.gold) return '—';
  return Number(mail.gold).toLocaleString() + ' (' + mailboxClaimLabel(mail.goldClaimed ? 1 : 0) + ')';
}

function mailboxAttachmentLabel(mail) {
  if (!mail.attachments || mail.attachments.length === 0) return '—';
  return mail.attachments.map((item) => {
    const name = item.name || ('#' + item.itemId);
    return `${mailboxItemName(item.itemId, name, item.rarity)} ×${Number(item.count).toLocaleString()}`
      + ` (${mailboxClaimLabel(item.claimedFlag)})`;
  }).join('<br>');
}

function mailboxStatusLabel(mail) {
  const parts = [mail.folder || '收件箱'];
  if (mail.expired) parts.push('已过期');
  parts.push(mail.read ? '已读' : '未读');
  return parts.join(' · ');
}

function mailboxExpireLabel(mail) {
  if (mail.unlimited) return '永久';
  if (mail.expired) return '已过期';
  if (mail.remainSeconds > 0) return formatRemainingTime(mail.remainSeconds);
  return mail.expireAt || '—';
}

function renderMailbox(data) {
  const body = $('#mail-table tbody');
  if (!body) return;
  const mails = (data && data.mails) || [];
  $('#mail-count').textContent = mails.length + ' 封';
  body.innerHTML = '';
  if (mails.length === 0) {
    body.innerHTML = '<tr><td colspan="8" class="hint">邮箱为空</td></tr>';
    return;
  }

  for (const mail of mails) {
    const tr = document.createElement('tr');
    const subject = mailboxSubject(mail);
    const bodyText = String(mail.body || '').trim();
    tr.innerHTML = `<td>${mail.messageId}</td>
      <td>${escapeHtml(mail.senderName || '系统')}</td>
      <td title="${escapeHtml(bodyText || subject)}">${escapeHtml(subject)}</td>
      <td>${mailboxGoldLabel(mail)}</td>
      <td class="mail-attachments">${mailboxAttachmentLabel(mail)}</td>
      <td>${escapeHtml(mailboxStatusLabel(mail))}</td>
      <td>${escapeHtml(mailboxExpireLabel(mail))}</td>
      <td><button class="mini danger">删除</button></td>`;
    tr.querySelector('button').onclick = () => deleteMailboxMessage(mail.messageId);
    body.appendChild(tr);
  }
}

// 邮箱页不在前台时不发请求, 切角色只把表格复位, 首次打开该页再拉取
function resetMailboxPanel(hint) {
  const body = $('#mail-table tbody');
  if (!body) return;
  $('#mail-count').textContent = '';
  body.innerHTML = `<tr><td colspan="8" class="hint">${escapeHtml(hint || '打开本页或点刷新以加载邮箱')}</td></tr>`;
}

async function loadMailbox() {
  const body = $('#mail-table tbody');
  if (!body) return;
  if (!currentChar) {
    resetMailboxPanel('请先选择角色');
    return;
  }
  const epoch = selectEpoch;
  try {
    const data = await api(`/api/characters/${currentChar.characterId}/mailbox`);
    if (epoch !== selectEpoch) return; // 期间又切了别的角色, 本次结果作废
    renderMailbox(data);
  } catch (e) {
    if (epoch !== selectEpoch) return;
    $('#mail-count').textContent = '';
    body.innerHTML = `<tr><td colspan="8" class="hint">${escapeHtml(e.message)}</td></tr>`;
    toast(e.message, true);
  }
}

async function deleteMailboxMessage(messageId) {
  if (!currentChar) return;
  if (!confirm(`删除邮件 #${messageId}？未领取的附件不会进背包, 此操作不可撤销。`)) return;
  try {
    const result = await post(`/api/characters/${currentChar.characterId}/mailbox/delete`, { messageId });
    toast(`已删除邮件 #${messageId}：附件 ${Number(result.attachmentCount || 0)}`
      + `、审计 ${Number(result.auditCount || 0)}`);
    loadMailbox();
  } catch (e) {
    toast(e.message, true);
  }
}
