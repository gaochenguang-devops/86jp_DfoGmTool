let runtimeReady = false;
let runtimeStatus = null;
let runtimeSourceEpoch = 0;
let runtimePollTimer = 0;
let runtimeConfiguring = false;
let runtimeLoggingIn = false;
const RUNTIME_SOURCE_STORAGE_KEY = 'dfo-gm-runtime-source';

function canChangeRuntimeSource() {
  return Boolean(runtimeStatus && runtimeStatus.canChangeSource);
}

function readStoredRuntimeSource() {
  try {
    const value = JSON.parse(localStorage.getItem(RUNTIME_SOURCE_STORAGE_KEY));
    if (!value || typeof value.databasePath !== 'string' || typeof value.pvfPath !== 'string') return null;

    const databasePath = value.databasePath.trim();
    const pvfPath = value.pvfPath.trim();
    // 图标目录是可选项, 老记录里没有这个字段
    const imagePacksPath = typeof value.imagePacksPath === 'string' ? value.imagePacksPath.trim() : '';
    return databasePath && pvfPath ? { databasePath, pvfPath, imagePacksPath } : null;
  } catch (_) {
    return null;
  }
}

function saveRuntimeSource(databasePath, pvfPath, imagePacksPath) {
  try {
    localStorage.setItem(RUNTIME_SOURCE_STORAGE_KEY,
      JSON.stringify({ databasePath, pvfPath, imagePacksPath: imagePacksPath || '' }));
  } catch (_) {
    // Source selection still works when browser storage is unavailable.
  }
}

function clearRuntimePoll() {
  if (runtimePollTimer) {
    clearTimeout(runtimePollTimer);
    runtimePollTimer = 0;
  }
}

function setRuntimeSourceState(text, isError) {
  const state = $('#runtime-source-state');
  state.textContent = text || '';
  state.className = isError ? 'hint err' : 'hint';
}

function setLoginState(text, isError) {
  const state = $('#login-state');
  state.textContent = text || '';
  state.className = isError ? 'hint err' : 'hint';
}

function updateRuntimeActionButtons(status) {
  $('#btn-runtime-source').classList.toggle('hidden', !(status && status.canChangeSource));
  $('#btn-logout').classList.toggle('hidden', !(status && status.authenticationRequired && status.authenticated));
}

function updateRuntimeSourceInputs(status, force) {
  const database = $('#runtime-database-path');
  const pvf = $('#runtime-pvf-path');
  const imagePacks = $('#runtime-imagepacks-path');
  const stored = readStoredRuntimeSource();
  const databasePath = String(status && status.database || '').trim()
    || (stored && stored.databasePath)
    || database.value;
  const pvfPath = String(status && status.pvf || '').trim()
    || (stored && stored.pvfPath)
    || pvf.value;
  const imagePacksPath = String(status && status.imagePacks || '').trim()
    || (stored && stored.imagePacksPath)
    || (imagePacks ? imagePacks.value : '');
  if (databasePath) database.value = databasePath;
  if (pvfPath) pvf.value = pvfPath;
  if (imagePacks && imagePacksPath) imagePacks.value = imagePacksPath;
}

function showLoginPanel() {
  hideRuntimeSourcePanel();
  $('#login-panel').classList.remove('hidden');
  setTimeout(() => $('#login-password').focus(), 0);
}

function hideLoginPanel() {
  $('#login-panel').classList.add('hidden');
}

function showRuntimeSourcePanel(forceValues) {
  if (!canChangeRuntimeSource()) return;
  updateRuntimeSourceInputs(runtimeStatus, forceValues);
  $('#runtime-source-panel').classList.remove('hidden');
  $('#btn-close-runtime-source').classList.toggle('hidden', !runtimeReady || runtimeConfiguring);
}

function hideRuntimeSourcePanel() {
  $('#runtime-source-panel').classList.add('hidden');
}

function resetRuntimeWorkspace() {
  if (typeof resetInventoryAnomalyState === 'function') resetInventoryAnomalyState();
  if (typeof resetAccountWorkspace === 'function') resetAccountWorkspace();
  giveCategory = null;
  giveNavExpanded.clear();
  $('#give-category-nav').innerHTML = '';
  $('#search-results tbody').innerHTML = '';
  $('#give-total').textContent = '';
  $('#workspace').classList.add('hidden');
  $('#runtime-notice').classList.add('hidden');
}

function stopRuntimeWorkspace() {
  if (!runtimeReady) return;
  runtimeReady = false;
  runtimeSourceEpoch++;
  resetRuntimeWorkspace();
}

function startRuntimeWorkspace() {
  const epoch = runtimeSourceEpoch;
  $('#workspace').classList.remove('hidden');
  $('#runtime-notice').classList.remove('hidden');
  hideRuntimeSourcePanel();
  loadGiveCategories(epoch).catch((e) => toast(e.message, true));
  loadAccounts(epoch).catch((e) => toast(e.message, true));
  if (typeof refreshInventoryAnomalyStatus === 'function')
    refreshInventoryAnomalyStatus(epoch);
}

function applyRuntimeStatus(status) {
  runtimeStatus = status;
  if (typeof updateA12A21MigrationEnvironment === 'function')
    updateA12A21MigrationEnvironment(status);
  const authenticationRequired = Boolean(status && status.authenticationRequired);
  const authenticated = !authenticationRequired || Boolean(status && status.authenticated);
  renderRuntimeStatus(status);
  updateRuntimeActionButtons(status);

  if (authenticationRequired && !authenticated) {
    clearRuntimePoll();
    stopRuntimeWorkspace();
    hideRuntimeSourcePanel();
    showLoginPanel();
    return;
  }

  hideLoginPanel();
  if (status && status.ready) {
    clearRuntimePoll();
    if (!runtimeReady) {
      runtimeReady = true;
      startRuntimeWorkspace();
    }
    return;
  }

  stopRuntimeWorkspace();
  if (status && status.error)
    setRuntimeSourceState(status.error, true);
  else if (status && status.hasError)
    setRuntimeSourceState('数据源加载失败', true);
  else if (status && status.loading)
    setRuntimeSourceState('PVF 索引构建中…', false);
  else
    setRuntimeSourceState('', false);

  if (canChangeRuntimeSource())
    showRuntimeSourcePanel(!status || !status.configured);
  else
    hideRuntimeSourcePanel();

  clearRuntimePoll();
  if (status && status.loading)
    runtimePollTimer = setTimeout(refreshRuntimeEnvironment, 1000);
}

function handleAuthenticationRequired() {
  if (!(runtimeStatus && runtimeStatus.authenticationRequired)) return;

  applyRuntimeStatus({
    configured: Boolean(runtimeStatus && runtimeStatus.configured),
    ready: false,
    loading: false,
    indexReady: false,
    authenticationRequired: true,
    authenticated: false,
    canChangeSource: false,
  });
}

async function refreshRuntimeEnvironment() {
  try {
    const status = await api('/api/status');
    applyRuntimeStatus(status);
    return status;
  } catch (e) {
    clearRuntimePoll();
    stopRuntimeWorkspace();
    runtimeStatus = null;
    renderRuntimeStatus(null);
    updateRuntimeActionButtons(null);
    hideRuntimeSourcePanel();
    hideLoginPanel();
    return null;
  }
}

async function configureRuntimeEnvironment() {
  if (runtimeConfiguring || !canChangeRuntimeSource()) return;

  const databasePath = $('#runtime-database-path').value.trim();
  const pvfPath = $('#runtime-pvf-path').value.trim();
  const imagePacksPath = ($('#runtime-imagepacks-path')?.value || '').trim();
  if (!databasePath || !pvfPath) {
    setRuntimeSourceState('请填写数据库和 PVF 路径', true);
    return;
  }

  saveRuntimeSource(databasePath, pvfPath, imagePacksPath);
  setRuntimeSourceState('正在加载…', false);
  runtimeConfiguring = true;
  $('#btn-load-runtime-source').disabled = true;
  $('#btn-close-runtime-source').classList.add('hidden');
  try {
    const result = await post('/api/environment', { databasePath, pvfPath, imagePacksPath });
    const classified = Boolean(result.migrationRequired || result.databaseUnusable);
    // 只改图标目录时后端不会重建索引(sourceChanged=false), 工作区保持原样, 原地补图即可
    const sourceChanged = result.sourceChanged !== false || classified;
    if (sourceChanged) {
      runtimeReady = false;
      runtimeSourceEpoch++;
      resetRuntimeWorkspace();
    }
    applyRuntimeStatus({
      ...result.status,
      authenticationRequired: false,
      authenticated: true,
      canChangeSource: true,
    });
    if (!sourceChanged && result.imagePacksChanged) {
      if (typeof refreshItemIcons === 'function') refreshItemIcons();
      setRuntimeSourceState(result.status && result.status.hasImagePacks
        ? '图标目录已更新'
        : '未启用图标：ImagePacks2 路径为空或无效，物品只显示文字', false);
    }
    if (result.migrationRequired) {
      if (typeof showA12A21MigrationRequired === 'function')
        showA12A21MigrationRequired(result.preview);
      const migrationError = result.migrationBlocked && result.preview && result.preview.error
        ? `迁移预览被阻止：${result.preview.error}`
        : '已识别可迁移旧库，数据库已释放，请预览/升级。';
      setRuntimeSourceState(migrationError, Boolean(result.migrationBlocked));
    } else if (result.databaseUnusable) {
      if (typeof showA12A21DatabaseUnusable === 'function')
        showA12A21DatabaseUnusable(result.error);
      setRuntimeSourceState('数据库不可用；请移除该文件等待服务端自动生成，或选择正确数据库。', true);
    } else if (classified) {
      setRuntimeSourceState(result.error || '数据源不可用', true);
    }
  } catch (e) {
    setRuntimeSourceState(e.message, true);
  } finally {
    runtimeConfiguring = false;
    $('#btn-load-runtime-source').disabled = false;
    if (runtimeReady) $('#btn-close-runtime-source').classList.remove('hidden');
  }
}

// 浏览器拿不到真实磁盘路径, 由后端弹本机选择框回填(仅本机模式, 非 Windows 会返回提示)
async function browseRuntimePath(kind, inputId) {
  if (runtimeConfiguring || !canChangeRuntimeSource()) return;

  const input = $(inputId);
  if (!input) return;
  try {
    setRuntimeSourceState('正在打开系统选择框…', false);
    const result = await post('/api/environment/browse', { kind, currentPath: input.value.trim() });
    if (result.cancelled || !result.path) {
      setRuntimeSourceState('', false);
      return;
    }
    input.value = result.path;
    setRuntimeSourceState('', false);
  } catch (e) {
    setRuntimeSourceState(e.message, true);
  }
}

async function loginRuntime() {
  if (runtimeLoggingIn) return;

  const password = $('#login-password').value;
  if (!password) {
    setLoginState('请输入密码', true);
    return;
  }

  runtimeLoggingIn = true;
  $('#btn-login').disabled = true;
  setLoginState('正在登录…', false);
  try {
    await post('/api/auth/login', { password });
    $('#login-password').value = '';
    setLoginState('', false);
    const status = await refreshRuntimeEnvironment();
    if (!status) {
      setLoginState('后端无响应', true);
      showLoginPanel();
    }
  } catch (e) {
    setLoginState(e.message, true);
  } finally {
    runtimeLoggingIn = false;
    $('#btn-login').disabled = false;
  }
}

async function logoutRuntime() {
  try {
    await post('/api/auth/logout');
    handleAuthenticationRequired();
    await refreshRuntimeEnvironment();
  } catch (e) {
    toast(e.message, true);
  }
}

function bindRuntimeEnvironment() {
  $('#btn-runtime-source').onclick = () => showRuntimeSourcePanel(true);
  $('#btn-logout').onclick = logoutRuntime;
  $('#btn-browse-database').onclick = () => browseRuntimePath('database', '#runtime-database-path');
  $('#btn-browse-pvf').onclick = () => browseRuntimePath('pvf', '#runtime-pvf-path');
  $('#btn-browse-imagepacks').onclick = () => browseRuntimePath('imagepacks', '#runtime-imagepacks-path');
  $('#btn-clear-imagepacks').onclick = () => {
    $('#runtime-imagepacks-path').value = '';
    setRuntimeSourceState('', false);
  };
  $('#btn-close-runtime-source').onclick = () => {
    if (runtimeReady && !runtimeConfiguring) hideRuntimeSourcePanel();
  };
  $('#runtime-source-form').onsubmit = (event) => {
    event.preventDefault();
    configureRuntimeEnvironment();
  };
  $('#login-form').onsubmit = (event) => {
    event.preventDefault();
    loginRuntime();
  };
}

async function initializeRuntimeEnvironment() {
  const status = await refreshRuntimeEnvironment();
  if (!status || status.authenticationRequired || status.configured || !status.canChangeSource) return;

  const source = readStoredRuntimeSource();
  if (!source) return;

  $('#runtime-database-path').value = source.databasePath;
  $('#runtime-pvf-path').value = source.pvfPath;
  const imagePacks = $('#runtime-imagepacks-path');
  if (imagePacks && source.imagePacksPath) imagePacks.value = source.imagePacksPath;
  return configureRuntimeEnvironment();
}
