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
        el('chkDisableSearch').checked = !!configuration.DisableSearch;
        el('chkEnableTraktCatalogs').checked = !!configuration.EnableTraktCatalogs;
        el('chkEnableNativeScraper').checked = !!configuration.EnableNativeScraper;
        el('chkEnableNativeAggregation').checked = !!configuration.EnableNativeAggregation;
        el('chkEnableTorBoxResolver').checked = !!configuration.EnableTorBoxResolver;
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
        loadSecretStates().catch(function (error) { console.error(error); });
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
          next.DisableSearch = checked('chkDisableSearch');
          next.EnableTraktCatalogs = checked('chkEnableTraktCatalogs');
          next.EnableNativeScraper = checked('chkEnableNativeScraper');
          next.EnableNativeAggregation = checked('chkEnableNativeAggregation');
          next.EnableTorBoxResolver = checked('chkEnableTorBoxResolver');
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
          Dashboard.processPluginConfigurationUpdateResult();
        } catch (error) { console.error(error); Dashboard.alert('Could not save configuration.'); }
        finally { Dashboard.hideLoadingMsg(); }
      }

      async function loadSecretStates() {
        var statuses = await window.ApiClient.getJSON(api('nebulabridge/provider-secrets'));
        secretStates = {};
        (statuses || []).forEach(function (status) { secretStates[value(status, 'Provider')] = value(status, 'HasKey') === true; });
        setupSecret('torbox', 'txtTorBoxApiToken');
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
      function renderCatalogs() {
        var host = el('catalogsList'); host.replaceChildren();
        var term = field('txtCatalogSearch', '').toLowerCase();
        var table = document.createElement('table'); table.className = 'table detailTable'; table.style.width = '100%';
        var header = document.createElement('tr'); header.innerHTML = '<th>Name</th><th>Type</th><th>Schedule</th><th>Enabled</th><th>Home row</th><th>Max</th><th></th>'; table.appendChild(header);
        catalogs.filter(function (cat) { return String(value(cat,'Name') || '').toLowerCase().indexOf(term) >= 0; }).forEach(function (cat) {
          var row = document.createElement('tr');
          var name = document.createElement('td'); name.textContent = value(cat,'Name') || value(cat,'Id'); row.appendChild(name);
          var type = document.createElement('td'); type.textContent = value(cat,'Type') || ''; row.appendChild(type);
          var schedule = document.createElement('td'); schedule.textContent = cadence(cat); row.appendChild(schedule);
          ['Enabled','ShowOnHome'].forEach(function (property) { var cell=document.createElement('td'); var check=document.createElement('input'); check.type='checkbox'; check.checked=value(cat,property) !== false && (property === 'ShowOnHome' || value(cat,property) === true); check.dataset.field=property; cell.appendChild(check); row.appendChild(cell); });
          var maxCell=document.createElement('td'); var max=document.createElement('input'); max.type='number'; max.className='emby-input'; max.style.width='5em'; max.value=value(cat,'MaxItems') || 100; max.dataset.field='MaxItems'; maxCell.appendChild(max); row.appendChild(maxCell);
          var action=document.createElement('td'); var button=document.createElement('button'); button.type='button'; button.className='raised emby-button'; button.textContent='Refresh'; button.onclick=function(){ triggerCatalog(cat); }; action.appendChild(button); row.appendChild(action);
          row.dataset.catalogKey = (value(cat,'Source') || 'stremio') + '|' + value(cat,'Type') + '|' + value(cat,'Id'); table.appendChild(row);
        });
        host.appendChild(table);
      }
      function collectCatalogs() {
        el('catalogsList').querySelectorAll('tr[data-catalog-key]').forEach(function (row) {
          var parts=row.dataset.catalogKey.split('|'); var cat=catalogs.find(function(c){return (value(c,'Source')||'stremio')===parts[0]&&value(c,'Type')===parts[1]&&value(c,'Id')===parts[2];}); if(!cat)return;
          cat.Enabled=row.querySelector('[data-field="Enabled"]').checked; cat.ShowOnHome=row.querySelector('[data-field="ShowOnHome"]').checked; cat.MaxItems=parseInt(row.querySelector('[data-field="MaxItems"]').value,10)||0;
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

      async function loadIndexers(){try{var result=await Promise.all([window.ApiClient.getJSON(api('nebulabridge/native-indexers')),window.ApiClient.getJSON(api('nebulabridge/native-indexers/status'))]);var definitions=result[0]||[];var status=result[1]||{};el('indexerDefinitionStatus').textContent=(value(status,'Message')||'')+' '+(value(status,'CompatibleCount')||0)+' of '+(value(status,'DefinitionCount')||0)+' compatible.';var host=el('nativeIndexerList');host.replaceChildren();definitions.forEach(function(item){var row=document.createElement('div');row.style.cssText='display:flex;gap:.75em;padding:.55em 0;border-bottom:1px solid rgba(255,255,255,.1)';var check=document.createElement('input');check.type='checkbox';check.checked=value(item,'Enabled')===true;check.disabled=value(item,'Compatible')!==true;check.onchange=async function(){try{await window.ApiClient.ajax({type:'POST',url:api('nebulabridge/native-indexers/'+encodeURIComponent(value(item,'Id'))+'/enabled'),data:JSON.stringify({Enabled:check.checked}),contentType:'application/json'});}catch(error){check.checked=!check.checked;}};var text=document.createElement('div');var strong=document.createElement('strong');strong.textContent=(value(item,'Name')||value(item,'Id'))+' — '+(value(item,'State')||'disabled');var description=document.createElement('div');description.className='fieldDescription';description.textContent=value(item,'Description')||value(item,'Error')||value(item,'Id');text.append(strong,description);row.append(check,text);host.appendChild(row);});}catch(error){console.error(error);el('indexerDefinitionStatus').textContent='Could not load indexers.';}}
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
      el('btnTraktConnect').onclick=connectTrakt;
      el('btnTraktDisconnect').onclick=disconnectTrakt;
      el('btnOpenJellyfinTrakt').onclick=function(){window.open(traktPage(),'_blank','noopener,noreferrer');};
      el('btnInstallJellyfinTrakt').onclick=function(){installOfficialTrakt().catch(function(error){console.error(error);Dashboard.alert(error.message||'Could not install Trakt.');});};
      setTab('general');
      loadConfiguration();
}
