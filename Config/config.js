export default function (page) {
      'use strict';
      var pluginId = 'e9d7c793-aee0-49b6-82c1-8ad583453663';
      var mask = '••••••••';
      if (!page) return;
      var form = page.querySelector('#nebulabridgeForm');
      var cfg = null;
      var catalogs = [];
      var userRows = [];
      var secretStates = {};
      var debridProviders = {};
      var traktTimer = null;

      function el(id) { return page.querySelector('#' + id); }
      function value(object, name) { return object && (object[name] !== undefined ? object[name] : object[name.charAt(0).toLowerCase() + name.slice(1)]); }
      function api(path) { return window.ApiClient.getUrl(path); }
      function parse(response) { return typeof response === 'string' ? JSON.parse(response) : response; }
      function guid(valueText) { return String(valueText || '').replace(/-/g, '').toLowerCase(); }
      function setTab(name) {
        page.querySelectorAll('.tab-button').forEach(function (button) {
          var active = button.dataset.tab === name;
          button.style.borderBottom = active ? '2px solid #00a4dc' : '2px solid transparent';
          button.style.opacity = active ? '1' : '.65';
        });
        page.querySelectorAll('.tab-content').forEach(function (section) { section.style.display = section.id === 'tab-' + name ? '' : 'none'; });
        if (name === 'catalogs') loadCatalogs();
        if (name === 'native-sources') loadIndexers();
        if (name === 'acquisition') loadAcquisitionJobs();
        if (name === 'kept-media') loadKeptMedia();
        if (name === 'user-access') loadUsers();
      }
      // Delegate tab clicks from the stable page root. Jellyfin may rebuild or restore
      // individual button nodes while navigating its cached single-page dashboard.
      page.onclick = function (event) {
        var target = event.target;
        var button = target && target.closest ? target.closest('.tab-button') : null;
        if (!button || !page.contains(button)) return;
        event.preventDefault();
        setTab(button.dataset.tab);
      };

      function field(id, fallback) { var node = el(id); return node ? node.value : fallback; }
      function checked(id) { var node = el(id); return !!(node && node.checked); }
      function assignFields(configuration) {
        el('txtMoviePath').value = configuration.MoviePath || '';
        el('txtSeriesPath').value = configuration.SeriesPath || '';
        el('txtMovieImportPath').value = configuration.MovieImportPath || '';
        el('txtSeriesImportPath').value = configuration.SeriesImportPath || '';
        el('chkDisableSearch').checked = !!configuration.DisableSearch;
        el('chkEnableTraktCatalogs').checked = !!configuration.EnableTraktCatalogs;
        el('chkEnableNativeScraper').checked = !!configuration.EnableNativeScraper;
        el('chkEnableNativeAggregation').checked = !!configuration.EnableNativeAggregation;
        el('txtFlareSolverrUrl').value = configuration.FlareSolverrUrl || '';
        el('txtStreamAddonUrls').value = (configuration.StreamAddonUrls || []).join('\n');
        el('txtStreamAddonTimeoutSeconds').value = configuration.StreamAddonTimeoutSeconds || 5;
        el('txtNativeResolvedStreamLimit').value = configuration.NativeResolvedStreamLimit || 10;
        el('chkEnableRemoteIndexerCatalog').checked = configuration.EnableRemoteIndexerCatalog !== false;
        el('txtIndexerCatalogManifestUrl').value = configuration.IndexerCatalogManifestUrl || 'https://indexers.watchastra.com/api/v1/indexers/manifest';
        el('txtIndexerCatalogPublicKey').value = configuration.IndexerCatalogPublicKey || '';
        el('txtTraktRedirectUri').value = configuration.TraktRedirectUri || '';
        el('chkEnableMixed').checked = !!configuration.EnableMixed;
        el('chkExtendLocalSeriesTrees').checked = !!configuration.ExtendLocalSeriesTrees;
        el('chkEnableJavaScriptInjection').checked = !!configuration.EnableJavaScriptInjection;
        el('chkLazyImages').checked = !!configuration.LazyImages;
        el('chkFilterUnreleased').checked = !!configuration.FilterUnreleased;
        el('txtBufferDays').value = configuration.FilterUnreleasedBufferDays || 0;
        el('txtStreamTTL').value = configuration.StreamTTL || 3600;
        el('txtDiscoveryDeadlineSeconds').value = configuration.DiscoveryDeadlineSeconds || 25;
        el('txtStreamOpenTimeoutSeconds').value = configuration.StreamOpenTimeoutSeconds || 20;
        el('chkEnableDiscoveryPrefetch').checked = configuration.EnableDiscoveryPrefetch !== false;
        el('txtDiscoveryPrefetchDepth').value = configuration.DiscoveryPrefetchDepth || 2;
        el('txtRawSearchLifetimeMinutes').value = configuration.RawSearchLifetimeMinutes || 360;
        el('txtDiscoveryLifetimeDays').value = configuration.DiscoveryLifetimeDays || 3;
        el('chkEnableLocalAcquisition').checked = configuration.EnableLocalAcquisition === true;
        el('txtPlaybackCacheRetentionDays').value = configuration.PlaybackCacheRetentionDays || 3;
        el('txtPlaybackCacheMinimumFreeSpaceMb').value = configuration.PlaybackCacheMinimumFreeSpaceMb || 2048;
        el('chkEnableDedicatedLog').checked = configuration.EnableDedicatedLog !== false;
        el('selDedicatedLogVerbosity').value = configuration.DedicatedLogVerbosity === undefined ? 0 : configuration.DedicatedLogVerbosity;
        el('txtFFmpegAnalyzeDuration').value = configuration.FFmpegAnalyzeDuration || '5M';
        el('txtFFmpegProbeSize').value = configuration.FFmpegProbeSize || '40M';
      }

      async function loadConfiguration() {
        Dashboard.showLoadingMsg();
        try {
          cfg = await window.ApiClient.getPluginConfiguration(pluginId);
          assignFields(cfg);
        } catch (error) { console.error(error); Dashboard.alert('Could not load Nebula Bridge configuration.'); }
        finally { Dashboard.hideLoadingMsg(); }

        // Provider and integration status is supplementary. Never hold Jellyfin's modal
        // loading overlay while these network calls run: a slow provider/plugin must not
        // prevent the administrator from changing tabs or editing core configuration.
        loadDebridProviders().catch(function (error) { console.error(error); });
        pollTrakt();
        loadOfficialTrakt();
      }

      async function saveConfiguration(event) {
        if (event) event.preventDefault();
        Dashboard.showLoadingMsg();
        try {
          var next = await window.ApiClient.getPluginConfiguration(pluginId);
          next.MoviePath = field('txtMoviePath', '').trim();
          next.SeriesPath = field('txtSeriesPath', '').trim();
          next.MovieImportPath = field('txtMovieImportPath', '').trim();
          next.SeriesImportPath = field('txtSeriesImportPath', '').trim();
          next.DisableSearch = checked('chkDisableSearch');
          next.EnableTraktCatalogs = checked('chkEnableTraktCatalogs');
          next.EnableNativeScraper = checked('chkEnableNativeScraper');
          next.EnableNativeAggregation = checked('chkEnableNativeAggregation');
          next.FlareSolverrUrl = field('txtFlareSolverrUrl', '').trim();
          next.StreamAddonUrls = field('txtStreamAddonUrls', '').split(/\r?\n/).map(function (line) { return line.trim(); }).filter(function (line) { return line.length > 0; });
          next.StreamAddonTimeoutSeconds = Math.max(1, Math.min(30, parseInt(field('txtStreamAddonTimeoutSeconds', '5'), 10) || 5));
          next.DebridProviders = collectDebridProviders(next.DebridProviders);
          next.EnableTorBoxResolver = next.DebridProviders.some(function (row) { return String(value(row, 'Id')).toLowerCase() === 'torbox' && value(row, 'Enabled') === true; });
          next.NativeResolvedStreamLimit = Math.max(1, Math.min(20, parseInt(field('txtNativeResolvedStreamLimit', '10'), 10) || 10));
          next.EnableRemoteIndexerCatalog = checked('chkEnableRemoteIndexerCatalog');
          next.IndexerCatalogManifestUrl = field('txtIndexerCatalogManifestUrl', '').trim();
          next.IndexerCatalogPublicKey = field('txtIndexerCatalogPublicKey', '').trim();
          next.TraktRedirectUri = field('txtTraktRedirectUri', '').trim();
          next.EnableMixed = checked('chkEnableMixed');
          next.ExtendLocalSeriesTrees = checked('chkExtendLocalSeriesTrees');
          next.EnableJavaScriptInjection = checked('chkEnableJavaScriptInjection');
          next.LazyImages = checked('chkLazyImages');
          next.FilterUnreleased = checked('chkFilterUnreleased');
          next.FilterUnreleasedBufferDays = parseInt(field('txtBufferDays', '0'), 10) || 0;
          next.StreamTTL = parseInt(field('txtStreamTTL', '3600'), 10) || 3600;
          next.DiscoveryDeadlineSeconds = parseInt(field('txtDiscoveryDeadlineSeconds', '25'), 10) || 25;
          next.StreamOpenTimeoutSeconds = parseInt(field('txtStreamOpenTimeoutSeconds', '20'), 10) || 20;
          next.EnableDiscoveryPrefetch = checked('chkEnableDiscoveryPrefetch');
          next.DiscoveryPrefetchDepth = parseInt(field('txtDiscoveryPrefetchDepth', '2'), 10) || 2;
          next.RawSearchLifetimeMinutes = Math.max(5, Math.min(10080, parseInt(field('txtRawSearchLifetimeMinutes', '360'), 10) || 360));
          next.DiscoveryLifetimeDays = Math.max(1, Math.min(365, parseInt(field('txtDiscoveryLifetimeDays', '3'), 10) || 3));
          next.EnableLocalAcquisition = checked('chkEnableLocalAcquisition');
          next.PlaybackCacheRetentionDays = Math.max(1, Math.min(365, parseInt(field('txtPlaybackCacheRetentionDays', '3'), 10) || 3));
          next.PlaybackCacheMinimumFreeSpaceMb = Math.max(256, Math.min(1048576, parseInt(field('txtPlaybackCacheMinimumFreeSpaceMb', '2048'), 10) || 2048));
          next.EnableDedicatedLog = checked('chkEnableDedicatedLog');
          next.DedicatedLogVerbosity = Math.max(0, Math.min(2, parseInt(field('selDedicatedLogVerbosity', '0'), 10) || 0));
          next.FFmpegAnalyzeDuration = field('txtFFmpegAnalyzeDuration', '5M').trim() || '5M';
          next.FFmpegProbeSize = field('txtFFmpegProbeSize', '40M').trim() || '40M';
          if (catalogs.length) { collectCatalogs(); next.Catalogs = catalogs; }
          await window.ApiClient.updatePluginConfiguration(pluginId, next);
          for (var catalogIndex = 0; catalogIndex < catalogs.length; catalogIndex++) {
            var savedCatalog = catalogs[catalogIndex];
            await window.ApiClient.ajax({
              type: 'POST',
              url: api('nebulabridge/catalogs/' + encodeURIComponent(value(savedCatalog, 'Id')) + '/' + encodeURIComponent(value(savedCatalog, 'Type')) + '/config'),
              data: JSON.stringify(savedCatalog),
              contentType: 'application/json'
            });
          }
          cfg = next;
          await saveUsers(false);
          loadIndexers().catch(function (error) { console.error(error); });
          Dashboard.processPluginConfigurationUpdateResult();
        } catch (error) { console.error(error); Dashboard.alert('Could not save configuration.'); }
        finally { Dashboard.hideLoadingMsg(); }
      }

      // Provider rows come from the orchestrator (health, capabilities, credential presence);
      // enabled/priority are edited locally and persisted with the plugin configuration.
      async function loadDebridProviders() {
        var host = el('debridProvidersList');
        try {
          var rows = await window.ApiClient.getJSON(api('nebulabridge/debrid-providers')) || [];
          debridProviders = {};
          rows.forEach(function (row) { debridProviders[value(row, 'Id')] = row; });
          renderDebridProviders(rows);
        } catch (error) {
          console.error(error);
          host.textContent = 'Could not load debrid providers.';
        }
        await loadSecretStates();
      }
      function configuredProvider(id) {
        var list = (cfg && cfg.DebridProviders) || [];
        for (var i = 0; i < list.length; i++) { if (String(value(list[i], 'Id')).toLowerCase() === id) return list[i]; }
        return null;
      }
      function healthLabel(row) {
        if (!value(row, 'HasCredential')) return 'No token';
        if (value(row, 'Enabled') !== true) return 'Disabled';
        var health = String(value(row, 'Health') || 'Healthy');
        var reason = value(row, 'LastFailureReason');
        var labels = { Healthy: 'Healthy', TemporarilyUnavailable: 'Temporarily unavailable', RateLimited: 'Rate limited', AuthFailed: 'Authentication failed', PremiumUnavailable: 'Premium unavailable' };
        var text = labels[health] || health;
        return reason && health !== 'Healthy' ? text + ' (' + reason + ')' : text;
      }
      function renderDebridProviders(rows) {
        var host = el('debridProvidersList'); host.replaceChildren();
        if (!rows.length) { host.textContent = 'No debrid providers are registered.'; return; }
        rows.forEach(function (row) {
          var id = String(value(row, 'Id'));
          var saved = configuredProvider(id);
          var enabled = saved ? value(saved, 'Enabled') === true : value(row, 'Enabled') === true;
          var priority = saved ? value(saved, 'Priority') : value(row, 'Priority');
          var card = document.createElement('div'); card.className = 'debridProvider'; card.dataset.provider = id;
          card.style.cssText = 'border:1px solid rgba(128,128,128,.35);border-radius:4px;padding:.75em 1em;margin:.5em 0';
          var head = document.createElement('div'); head.style.cssText = 'display:flex;flex-wrap:wrap;gap:1em;align-items:center';
          var toggle = document.createElement('label'); toggle.className = 'checkboxContainer'; toggle.style.margin = '0';
          var chk = document.createElement('input'); chk.setAttribute('is', 'emby-checkbox'); chk.type = 'checkbox'; chk.id = 'chkDebrid-' + id; chk.checked = enabled;
          var title = document.createElement('span'); title.textContent = String(value(row, 'Name') || id);
          toggle.appendChild(chk); toggle.appendChild(title); head.appendChild(toggle);
          var prio = document.createElement('label'); prio.style.cssText = 'display:flex;align-items:center;gap:.4em';
          prio.appendChild(document.createTextNode('Priority'));
          var num = document.createElement('input'); num.className = 'emby-input'; num.type = 'number'; num.min = '1'; num.max = '100'; num.style.width = '5em'; num.id = 'txtDebridPriority-' + id; num.value = priority || 100;
          prio.appendChild(num); head.appendChild(prio);
          var status = document.createElement('span'); status.className = 'fieldDescription'; status.id = 'debridStatus-' + id; status.style.margin = '0'; status.textContent = 'Status: ' + healthLabel(row);
          head.appendChild(status);
          var test = document.createElement('button'); test.type = 'button'; test.className = 'raised emby-button'; test.textContent = 'Test';
          test.onclick = function () { testDebridProvider(id, status, test); };
          head.appendChild(test);
          card.appendChild(head);
          var caps = document.createElement('div'); caps.className = 'fieldDescription';
          caps.textContent = 'Capabilities: ' + ((value(row, 'Capabilities') || []).join(', ') || 'none');
          card.appendChild(caps);
          var tokenWrap = document.createElement('div'); tokenWrap.className = 'inputContainer'; tokenWrap.style.marginTop = '.5em';
          var label = document.createElement('label'); label.className = 'inputLabel'; label.htmlFor = 'txtDebridToken-' + id; label.textContent = title.textContent + ' API token';
          var input = document.createElement('input'); input.className = 'emby-input'; input.type = 'password'; input.autocomplete = 'new-password'; input.id = 'txtDebridToken-' + id;
          var note = document.createElement('div'); note.className = 'fieldDescription';
          note.textContent = value(row, 'CredentialFromEnvironment') ? 'Provided by environment variable; the saved value is ignored.' : 'Write-only. The saved token is never shown again.';
          var actions = document.createElement('div'); actions.className = 'secretActions'; actions.dataset.provider = id;
          tokenWrap.appendChild(label); tokenWrap.appendChild(input); tokenWrap.appendChild(note); tokenWrap.appendChild(actions);
          card.appendChild(tokenWrap);
          host.appendChild(card);
        });
      }
      function collectDebridProviders(existing) {
        var result = [];
        var seen = {};
        Object.keys(debridProviders).forEach(function (id) {
          var chk = el('chkDebrid-' + id);
          if (!chk) return;
          var priority = parseInt(field('txtDebridPriority-' + id, '100'), 10) || 100;
          result.push({ Id: id, Enabled: chk.checked, Priority: Math.max(1, Math.min(100, priority)) });
          seen[id] = true;
        });
        // Keep rows for providers that are not registered in this build (e.g. a disabled assembly).
        (existing || []).forEach(function (row) {
          var id = String(value(row, 'Id') || '').toLowerCase();
          if (id && !seen[id]) result.push(row);
        });
        return result;
      }
      async function testDebridProvider(id, status, button) {
        button.disabled = true; status.textContent = 'Status: testing…';
        try {
          var result = await window.ApiClient.ajax({ type: 'POST', url: api('nebulabridge/debrid-providers/' + encodeURIComponent(id) + '/test'), dataType: 'json' });
          status.textContent = value(result, 'Ok') ? 'Status: connected' : 'Status: ' + (value(result, 'Message') || 'failed') + ' (' + (value(result, 'Reason') || 'error') + ')';
        } catch (error) { console.error(error); status.textContent = 'Status: test request failed'; }
        finally { button.disabled = false; }
      }

      async function loadSecretStates() {
        var statuses = await window.ApiClient.getJSON(api('nebulabridge/provider-secrets'));
        secretStates = {};
        (statuses || []).forEach(function (status) { secretStates[value(status, 'Provider')] = value(status, 'HasKey') === true; });
        Object.keys(debridProviders).forEach(function (id) { setupSecret(id, 'txtDebridToken-' + id); });
        setupSecret('trakt-client-id', 'txtTraktClientId');
        setupSecret('trakt-client-secret', 'txtTraktClientSecret');
      }
      function setupSecret(provider, inputId) {
        var input = el(inputId);
        var actions = page.querySelector('.secretActions[data-provider="' + provider + '"]');
        if (!input || !actions) return;
        input.value = secretStates[provider] ? mask : '';
        input.onfocus = function () { if (input.value === mask) input.select(); };
        actions.replaceChildren();
        var save = document.createElement('button');
        save.type = 'button'; save.className = 'raised emby-button'; save.textContent = secretStates[provider] ? 'Save new key' : 'Save key';
        save.onclick = function () { saveSecret(provider, inputId); };
        actions.appendChild(save);
        if (secretStates[provider]) {
          var clear = document.createElement('button');
          clear.type = 'button'; clear.className = 'emby-button'; clear.textContent = 'Clear key'; clear.style.marginLeft = '.5em';
          clear.onclick = function () { clearSecret(provider); };
          actions.appendChild(clear);
        }
      }
      async function saveSecret(provider, inputId) {
        var replacement = el(inputId).value.trim();
        if (!replacement || replacement === mask) { Dashboard.alert('Type a replacement value first.'); return false; }
        await window.ApiClient.ajax({ type:'PUT', url:api('nebulabridge/provider-secrets/' + encodeURIComponent(provider)), data:JSON.stringify({Value:replacement}), contentType:'application/json' });
        await loadSecretStates();
        return true;
      }
      async function clearSecret(provider) {
        if (!confirm('Clear the saved ' + provider + ' key? Environment-provided values are not affected.')) return;
        await window.ApiClient.ajax({ type:'DELETE', url:api('nebulabridge/provider-secrets/' + encodeURIComponent(provider)) });
        await loadSecretStates();
      }

      async function loadCatalogs() {
        try { catalogs = await window.ApiClient.getJSON(api('nebulabridge/catalogs')) || []; renderCatalogs(); }
        catch (error) { console.error(error); el('catalogsList').textContent = 'Could not load catalogs.'; }
      }
      function cadence(cat) {
        var id = String(value(cat, 'Id') || '').toLowerCase();
        if (id.indexOf('next') >= 0) return '1 AM + 1 PM';
        if (id.indexOf('trending') >= 0 || id.indexOf('box-office') >= 0) return 'Daily';
        return 'Weekly';
      }
      function isNextEpisodes(cat) { return String(value(cat, 'Id') || '').toLowerCase().indexOf('next') >= 0; }
      function libraryName(cat, separate) {
        if (isNextEpisodes(cat)) return 'Trakt Next Episodes';
        if (separate === undefined ? value(cat, 'SeparateLibrary') === true : separate) return value(cat, 'Name') || value(cat, 'Id');
        return String(value(cat, 'Type') || '').toLowerCase() === 'series' ? 'Shows' : 'Movies';
      }
      function renderCatalogs() {
        var host = el('catalogsList'); host.replaceChildren();
        var term = field('txtCatalogSearch', '').toLowerCase();
        var table = document.createElement('table'); table.className = 'table detailTable'; table.style.width = '100%';
        var header = document.createElement('tr'); header.innerHTML = '<th>Name</th><th>Type</th><th>Library</th><th>Schedule</th><th>Enabled</th><th>Own folder</th><th>Home row</th><th>Max</th><th></th>'; table.appendChild(header);
        catalogs.filter(function (cat) { return String(value(cat,'Name') || '').toLowerCase().indexOf(term) >= 0; }).forEach(function (cat) {
          var row = document.createElement('tr');
          var name = document.createElement('td'); name.textContent = value(cat,'Name') || value(cat,'Id'); row.appendChild(name);
          var type = document.createElement('td'); type.textContent = value(cat,'Type') || ''; row.appendChild(type);
          var library = document.createElement('td'); library.textContent = libraryName(cat); row.appendChild(library);
          var schedule = document.createElement('td'); schedule.textContent = cadence(cat); row.appendChild(schedule);
          ['Enabled','SeparateLibrary','ShowOnHome'].forEach(function (property) {
            var cell=document.createElement('td'); var check=document.createElement('input'); check.type='checkbox';
            check.checked = property === 'ShowOnHome' ? value(cat,property) !== false : value(cat,property) === true;
            check.dataset.field=property;
            if (property === 'SeparateLibrary' && isNextEpisodes(cat)) { check.disabled = true; check.checked = true; }
            if (property === 'SeparateLibrary') { check.onchange = function () { library.textContent = libraryName(cat, check.checked); }; }
            cell.appendChild(check); row.appendChild(cell);
          });
          var maxCell=document.createElement('td'); var max=document.createElement('input'); max.type='number'; max.className='emby-input'; max.style.width='5em'; max.value=value(cat,'MaxItems') || 100; max.dataset.field='MaxItems'; maxCell.appendChild(max); row.appendChild(maxCell);
          var action=document.createElement('td'); var button=document.createElement('button'); button.type='button'; button.className='raised emby-button'; button.textContent='Refresh'; button.onclick=function(){ triggerCatalog(cat); }; action.appendChild(button); row.appendChild(action);
          row.dataset.catalogKey = (value(cat,'Source') || 'stremio') + '|' + value(cat,'Type') + '|' + value(cat,'Id'); table.appendChild(row);
        });
        host.appendChild(table);
      }
      function collectCatalogs() {
        el('catalogsList').querySelectorAll('tr[data-catalog-key]').forEach(function (row) {
          var parts=row.dataset.catalogKey.split('|'); var cat=catalogs.find(function(c){return (value(c,'Source')||'stremio')===parts[0]&&value(c,'Type')===parts[1]&&value(c,'Id')===parts[2];}); if(!cat)return;
          cat.Enabled=row.querySelector('[data-field="Enabled"]').checked; cat.SeparateLibrary=!isNextEpisodes(cat)&&row.querySelector('[data-field="SeparateLibrary"]').checked; cat.ShowOnHome=row.querySelector('[data-field="ShowOnHome"]').checked; cat.MaxItems=parseInt(row.querySelector('[data-field="MaxItems"]').value,10)||0;
        });
      }
      async function triggerCatalog(cat) { collectCatalogs(); await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/catalogs/'+encodeURIComponent(value(cat,'Id'))+'/'+encodeURIComponent(value(cat,'Type'))+'/config'),data:JSON.stringify(cat),contentType:'application/json'}); await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/catalogs/'+encodeURIComponent(value(cat,'Id'))+'/'+encodeURIComponent(value(cat,'Type'))+'/import')}); Dashboard.alert('Catalog refresh started.'); }
      async function triggerAll() { collectCatalogs(); await Promise.all(catalogs.map(function(cat){return window.ApiClient.ajax({type:'POST',url:api('nebulabridge/catalogs/'+encodeURIComponent(value(cat,'Id'))+'/'+encodeURIComponent(value(cat,'Type'))+'/config'),data:JSON.stringify(cat),contentType:'application/json'});})); await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/catalogs/import-all')}); Dashboard.alert('Enabled catalog refreshes were queued.'); }

      async function loadUsers() { try { userRows=await window.ApiClient.getJSON(api('nebulabridge/user-access'))||[]; renderUsers(); } catch(error){console.error(error);el('userAccessGrid').textContent='Could not load users.';} }
      function renderUsers() {
        var host=el('userAccessGrid'); host.replaceChildren(); var table=document.createElement('table'); table.className='table detailTable'; table.style.width='100%'; var header=document.createElement('tr'); header.innerHTML='<th>User</th><th>No Nebula Bridge</th><th>Local search only</th><th>Notes</th>'; table.appendChild(header);
        userRows.forEach(function(row){var tr=document.createElement('tr');tr.dataset.userId=value(row,'UserId');var name=document.createElement('td');name.textContent=(value(row,'UserName')||'')+(value(row,'IsDisabled')?' (disabled)':'');tr.appendChild(name);['NoNebulaBridge','LocalSearchOnly'].forEach(function(prop){var td=document.createElement('td');var check=document.createElement('input');check.type='checkbox';check.checked=value(row,prop)===true;check.dataset.field=prop;td.appendChild(check);tr.appendChild(td);});var notesCell=document.createElement('td');var notes=document.createElement('input');notes.type='text';notes.className='emby-input';notes.value=value(row,'Notes')||'';notes.dataset.field='Notes';notes.setAttribute('aria-label','Notes for '+value(row,'UserName'));notesCell.appendChild(notes);tr.appendChild(notesCell);table.appendChild(tr);}); host.appendChild(table);
      }
      function collectUsers(){el('userAccessGrid').querySelectorAll('tr[data-user-id]').forEach(function(tr){var row=userRows.find(function(r){return String(value(r,'UserId')).replace(/-/g,'')===String(tr.dataset.userId).replace(/-/g,'');});if(!row)return;row.NoNebulaBridge=tr.querySelector('[data-field="NoNebulaBridge"]').checked;row.LocalSearchOnly=tr.querySelector('[data-field="LocalSearchOnly"]').checked;row.Notes=tr.querySelector('[data-field="Notes"]').value;});}
      async function saveUsers(showMessage){if(!userRows.length)return;collectUsers();await window.ApiClient.ajax({type:'PUT',url:api('nebulabridge/user-access'),data:JSON.stringify(userRows),contentType:'application/json'});if(showMessage)Dashboard.alert('Nebula Bridge user access saved.');}

      async function loadIndexers(){try{var result=await Promise.all([window.ApiClient.getJSON(api('nebulabridge/native-indexers')),window.ApiClient.getJSON(api('nebulabridge/native-indexers/status'))]);var definitions=result[0]||[];var status=result[1]||{};el('indexerDefinitionStatus').textContent=(value(status,'Message')||'')+' '+(value(status,'AvailableCount')||0)+' available; '+(value(status,'CompatibleCount')||0)+' of '+(value(status,'DefinitionCount')||0)+' compatible.';var host=el('nativeIndexerList');host.replaceChildren();definitions.forEach(function(item){var available=value(item,'Available')!==false;var row=document.createElement('div');row.style.cssText='display:flex;gap:.75em;padding:.55em 0;border-bottom:1px solid rgba(255,255,255,.1);opacity:'+(available?'1':'.5');var check=document.createElement('input');check.type='checkbox';check.checked=value(item,'Enabled')===true;check.disabled=value(item,'Compatible')!==true||!available;check.onchange=async function(){try{await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/native-indexers/'+encodeURIComponent(value(item,'Id'))+'/enabled'),data:JSON.stringify({Enabled:check.checked}),contentType:'application/json'});}catch(error){check.checked=!check.checked;}};var text=document.createElement('div');var strong=document.createElement('strong');strong.textContent=(value(item,'Name')||value(item,'Id'))+' — '+(value(item,'State')||'disabled');var description=document.createElement('div');description.className='fieldDescription';description.textContent=(!available&&value(item,'Error'))||value(item,'Description')||value(item,'Error')||value(item,'Id');text.append(strong,description);row.append(check,text);host.appendChild(row);});}catch(error){console.error(error);el('indexerDefinitionStatus').textContent='Could not load indexers.';}}

      function formatBytes(bytes) { var n=Number(bytes||0); if(n<1024)return n+' B'; if(n<1048576)return (n/1024).toFixed(1)+' KiB'; if(n<1073741824)return (n/1048576).toFixed(1)+' MiB'; return (n/1073741824).toFixed(2)+' GiB'; }
      function acquisitionButton(label, enabled, action) { var button=document.createElement('button');button.type='button';button.className='raised emby-button';button.textContent=label;button.disabled=!enabled;button.onclick=action;return button; }
      async function acquisitionAction(path, method) { try { await window.ApiClient.ajax({type:method,url:api(path)}); await loadAcquisitionJobs(); } catch(error) { console.error(error); Dashboard.alert('The acquisition action could not be completed in the current job state.'); } }
      async function loadAcquisitionJobs(){
        var host=el('acquisitionJobs');
        try{
          var response=await window.ApiClient.getJSON(api('nebulabridge/acquisition-cache'))||{};var enabled=value(response,'Enabled')===true;var jobs=value(response,'Jobs')||[];
          el('acquisitionStatus').textContent=(enabled?'Caching is enabled. ':'Caching is disabled; existing state is shown read-only. ')+jobs.length+' job(s).';host.replaceChildren();
          if(!jobs.length){var empty=document.createElement('div');empty.className='fieldDescription';empty.textContent='No playback cache jobs have been recorded.';host.appendChild(empty);return;}
          var table=document.createElement('table');table.className='table detailTable acquisition-table';var header=document.createElement('tr');header.innerHTML='<th>Item</th><th>State</th><th>Retention</th><th>Cached</th><th>Last access / expiry</th><th>Actions</th>';table.appendChild(header);
          jobs.forEach(function(job){var row=document.createElement('tr');var title=document.createElement('td');var identity=(value(job,'MediaType')||'Item')+': '+(value(job,'Title')||value(job,'ItemId'));if(value(job,'MediaType')==='Episode'){var season=value(job,'SeasonNumber'),episode=value(job,'EpisodeNumber'),series=value(job,'SeriesId');if(season!==null&&episode!==null)identity+=' — S'+String(season).padStart(2,'0')+'E'+String(episode).padStart(2,'0');if(series)identity+=' — '+series;}title.textContent=identity;row.appendChild(title);var state=document.createElement('td');state.textContent=value(job,'State')+(value(job,'Failure')?' — '+value(job,'Failure'):'');row.appendChild(state);var retention=document.createElement('td');retention.textContent=value(job,'Retention');row.appendChild(retention);var cached=document.createElement('td');var expected=value(job,'ExpectedBytes');cached.textContent=formatBytes(value(job,'CachedBytes'))+(expected?' / '+formatBytes(expected)+(value(job,'Percent')!==null?' ('+value(job,'Percent')+'%)':''):'');row.appendChild(cached);var dates=document.createElement('td');dates.textContent=(value(job,'LastUsefulAccess')||'—')+(value(job,'ExpiresUtc')?' / '+value(job,'ExpiresUtc'):'');row.appendChild(dates);var actions=document.createElement('td');var actionGroup=document.createElement('div');actionGroup.className='acquisition-actions';var id=encodeURIComponent(value(job,'Id'));actionGroup.appendChild(acquisitionButton(value(job,'MediaType')==='Movie'?'Keep Movie':'Keep Episode',enabled&&value(job,'CanKeepItem')===true,function(){acquisitionAction('nebulabridge/acquisition-cache/'+id+'/retain/KeepItem','POST');}));if(value(job,'MediaType')==='Episode')actionGroup.appendChild(acquisitionButton('Keep Series',enabled&&value(job,'CanKeepSeries')===true,function(){acquisitionAction('nebulabridge/acquisition-cache/'+id+'/retain/KeepSeries','POST');}));actionGroup.appendChild(acquisitionButton('Retry',enabled&&value(job,'CanRetry')===true,function(){acquisitionAction('nebulabridge/acquisition-cache/'+id+'/retry','POST');}));actionGroup.appendChild(acquisitionButton('Cancel',enabled&&value(job,'CanCancel')===true,function(){acquisitionAction('nebulabridge/acquisition-cache/'+id+'/cancel','POST');}));actionGroup.appendChild(acquisitionButton('Purge',value(job,'CanPurge')===true,function(){if(confirm('Delete this temporary cache?'))acquisitionAction('nebulabridge/acquisition-cache/'+id,'DELETE');}));actions.appendChild(actionGroup);row.appendChild(actions);table.appendChild(row);});host.appendChild(table);
        }catch(error){console.error(error);host.textContent='Could not load acquisition jobs.';}
      }
      function renderKeptTable(host, headers, rows) {
        host.replaceChildren();
        if (!rows.length) { var empty=document.createElement('div');empty.className='fieldDescription';empty.textContent='None.';host.appendChild(empty);return; }
        var table=document.createElement('table');table.className='table detailTable acquisition-table';var header=document.createElement('tr');header.innerHTML=headers.map(function(text){return '<th>'+text+'</th>';}).join('');table.appendChild(header);
        rows.forEach(function(cells){var row=document.createElement('tr');cells.forEach(function(cell){var td=document.createElement('td');td.textContent=cell;row.appendChild(td);});table.appendChild(row);});host.appendChild(table);
      }
      async function loadKeptMedia(){
        try{
          var response=await window.ApiClient.getJSON(api('nebulabridge/acquisition-cache/kept'))||{};var imported=value(response,'Imported')||[];var policies=value(response,'SeriesPolicies')||[];
          el('keptMediaStatus').textContent=imported.length+' recent imported item(s) shown; '+policies.length+' Keep Series polic'+(policies.length===1?'y.':'ies.');
          renderKeptTable(el('keptSeriesPolicies'),['Series','Imported episodes','Pending import','Last activity'],policies.map(function(policy){return [value(policy,'Title')||value(policy,'SeriesId'),String(value(policy,'ImportedEpisodes')||0),String(value(policy,'PendingEpisodes')||0),value(policy,'LastActivity')||'—'];}));
          renderKeptTable(el('keptMediaItems'),['Item','Type','Retention','State','Last useful access'],imported.map(function(job){return [value(job,'Title')||value(job,'ItemId'),value(job,'MediaType')||'Item',value(job,'Retention')||'—',value(job,'State')||'Imported',value(job,'LastUsefulAccess')||'—'];}));
        }catch(error){console.error(error);el('keptMediaStatus').textContent='Could not load kept local media.';el('keptSeriesPolicies').replaceChildren();el('keptMediaItems').replaceChildren();}
      }
      async function updateIndexers(){var result=parse(await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/native-indexers/refresh')}));Dashboard.alert(value(result,'Message')||'Indexer update complete.');await loadIndexers();}
      async function testIndexers(){el('nativeTestOutput').textContent='Running…';try{var result=parse(await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/native-indexers/search'),data:JSON.stringify({DefinitionId:null,Query:{Title:field('txtNativeTestQuery','').trim()}}),contentType:'application/json'}));el('nativeTestOutput').textContent=JSON.stringify(result,null,2);}catch(error){console.error(error);el('nativeTestOutput').textContent='Test failed. Save and enable the scraper, then check the Jellyfin log.';}}

      function renderTrakt(status){var state=value(status,'State')||'disconnected';el('traktConnectionStatus').textContent=value(status,'Message')||(state==='connected'?'Connected'+(value(status,'ConnectedUser')?' as '+value(status,'ConnectedUser'):'')+'.':'Not connected.');var pending=state==='pending';el('traktActivationPanel').style.display=pending?'':'none';if(pending){el('traktUserCode').textContent=value(status,'UserCode')||'';el('traktVerificationUrl').href=value(status,'VerificationUrl')||'https://trakt.tv/activate';el('traktActivationLink').href=value(status,'ActivationUrl')||el('traktVerificationUrl').href;el('traktQrCode').src=value(status,'QrCodeDataUri')||'';if(traktTimer)clearTimeout(traktTimer);traktTimer=setTimeout(pollTrakt,3000);}else if(traktTimer){clearTimeout(traktTimer);traktTimer=null;}}
      async function pollTrakt(){try{renderTrakt(await window.ApiClient.getJSON(api('nebulabridge/trakt/device/status')));}catch(error){console.error(error);}}
      async function connectTrakt(){await saveConfiguration();if(field('txtTraktClientId','')!==mask&&field('txtTraktClientId','').trim())await saveSecret('trakt-client-id','txtTraktClientId');if(field('txtTraktClientSecret','')!==mask&&field('txtTraktClientSecret','').trim())await saveSecret('trakt-client-secret','txtTraktClientSecret');renderTrakt(parse(await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/trakt/device/start')})));}
      async function disconnectTrakt(){await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/trakt/disconnect')});renderTrakt({State:'disconnected'});}
      function traktPage(){return window.ApiClient.serverAddress().replace(/\/$/,'')+'/web/#/configurationpage?name=trakt';}
      async function loadOfficialTrakt(){try{var plugins=await window.ApiClient.getJSON(api('Plugins'));var installed=(plugins||[]).some(function(p){return guid(value(p,'Id'))==='4fe3201ed6ae4f2e8917e12bda571281'||String(value(p,'Name')||'').toLowerCase()==='trakt';});el('traktOfficialStatus').textContent=installed?'Jellyfin’s official Trakt plugin is installed. Authorize a user there and Nebula Bridge will inherit it automatically.':'Jellyfin’s official Trakt plugin is not installed.';el('btnInstallJellyfinTrakt').style.display=installed?'none':'';el('btnOpenJellyfinTrakt').style.display=installed?'':'none';}catch(error){console.error(error);}}
      async function installOfficialTrakt(){var packages=await window.ApiClient.getJSON(api('Packages'));var pkg=(packages||[]).find(function(p){return String(value(p,'Name')||'').toLowerCase()==='trakt';});var release=pkg&&(value(pkg,'Versions')||[])[0];if(!release)throw new Error('Official Trakt package not found.');var url=api('Packages/Installed/'+encodeURIComponent(value(pkg,'Name')))+'?AssemblyGuid='+encodeURIComponent(value(pkg,'Guid'))+'&version='+encodeURIComponent(value(release,'Version')||value(release,'VersionNumber'));await window.ApiClient.ajax({type:'POST',url:url});await window.ApiClient.ajax({type:'POST',url:api('System/Restart')}).catch(function(){});Dashboard.alert('Trakt was installed and Jellyfin is restarting. Reload this page after the server returns.');}

      form.onsubmit=saveConfiguration;
      el('txtCatalogSearch').oninput=renderCatalogs;
      el('btnRefreshCatalogs').onclick=loadCatalogs;
      el('btnImportAll').onclick=triggerAll;
      el('btnSaveUserAccess').onclick=function(){saveUsers(true);};
      el('btnUpdateIndexers').onclick=updateIndexers;
      el('btnNativeTest').onclick=testIndexers;
      el('btnRefreshAcquisition').onclick=loadAcquisitionJobs;
      el('btnRefreshKeptMedia').onclick=loadKeptMedia;
      el('btnTraktConnect').onclick=connectTrakt;
      el('btnTraktDisconnect').onclick=disconnectTrakt;
      el('btnOpenJellyfinTrakt').onclick=function(){window.open(traktPage(),'_blank','noopener,noreferrer');};
      el('btnInstallJellyfinTrakt').onclick=function(){installOfficialTrakt().catch(function(error){console.error(error);Dashboard.alert(error.message||'Could not install Trakt.');});};
      setTab('general');
      loadConfiguration();
}
