﻿import app = require("durandal/app");
import router = require("plugins/router");
import appUrl = require("common/appUrl");
import database = require("models/database");
import viewModelBase = require("viewmodels/viewModelBase");
import changesApi = require('common/changesApi');
import shell = require('viewmodels/shell');
import license = require("models/license");
import alert = require("models/alert");
import resource = require("models/resource");
import getOperationAlertsCommand = require("commands/getOperationAlertsCommand");
import dismissAlertCommand = require("commands/dismissAlertCommand");

import filesystem = require("models/filesystem/filesystem");

class resources extends viewModelBase {

    resources: KnockoutComputed<resource[]>;

    databases = ko.observableArray<database>();
    fileSystems = ko.observableArray<filesystem>();
    searchText = ko.observable("");
    visibleResources = ko.observable('');
    selectedResource = ko.observable<resource>();
    fileSystemsStatus = ko.observable<string>("loading");
	isAnyResourceSelected: KnockoutComputed<boolean>;
	hasAllResourcesSelected: KnockoutComputed<boolean>;
    allCheckedResourcesDisabled: KnockoutComputed<boolean>;
    systemDb: database;
    optionsClicked = ko.observable<boolean>(false);
    appUrls: computedAppUrls;
    alerts = ko.observable<alert[]>([]);

    constructor() {
        super();

        this.databases = shell.databases;
        this.fileSystems = shell.fileSystems;
        this.resources = shell.resources;
        
        this.systemDb = appUrl.getSystemDatabase();
        this.appUrls = appUrl.forCurrentDatabase(); 
        this.searchText.extend({ throttle: 200 }).subscribe(() => this.filterResources());

        var currentDatabse = this.activeDatabase();
        if (!!currentDatabse) {
            this.selectResource(currentDatabse, false);
        }

        var currentFileSystem = this.activeFilesystem();
        if (!!currentFileSystem) {
            this.selectResource(currentFileSystem, false);
        }

        var updatedUrl = appUrl.forResources();
        this.updateUrl(updatedUrl);

        this.hasAllResourcesSelected = ko.computed(() => {
            var resources = this.resources();
            for (var i = 0; i < resources.length; i++) {
                var rs: resource = resources[i];
                if (rs.isDatabase() && (<any>rs).isSystem) {
                    continue;
                }

                if (!rs.isVisible()) {
                    continue;
                }

                if (rs.isChecked() == false) {
                    return false;
                }
            }
            return true;
        });

        this.isAnyResourceSelected = ko.computed(() => {
            var resources = this.resources();
            for (var i = 0; i < resources.length; i++) {
                var rs: resource = resources[i];
                if (rs.isDatabase() && (<any>rs).isSystem) {
                    continue;
                }

                if (rs.isChecked()) {
                    return true;
                }
            }
            return false;
        });

        this.allCheckedResourcesDisabled = ko.computed(() => {
            var disabledStatus = null;
            for (var i = 0; i < this.resources().length; i++) {
                var rs: resource = this.resources()[i];
                if (rs.isChecked()) {
                    if (disabledStatus == null) {
                        disabledStatus = rs.disabled();
                    } else if (disabledStatus != rs.disabled()) {
                        return null;
                    }
                }
            }

            return disabledStatus;
        });
        this.fetchAlerts();
        this.visibleResources.subscribe(() => this.filterResources());
    }

    private fetchAlerts() {
        new getOperationAlertsCommand(appUrl.getSystemDatabase())
            .execute()
            .then((result: alert[]) => {
                this.alerts(result);
            });
    }

    // Override canActivate: we can always load this page, regardless of any system db prompt.
    canActivate(args: any): any {
        return true;
    }

    attached() {
        ko.postbox.publish("SetRawJSONUrl", appUrl.forDatabasesRawData());
        this.resourcesLoaded();
    }

    private resourcesLoaded() {
        // If we have no databases (except system db), show the "create a new database" screen.
        if (this.resources().length === 1) {
            this.newResource();
        }
    }

    filterResources() {
        var filter = this.searchText();
        var filterLower = filter.toLowerCase();
        this.databases().forEach((db: database) => {
            var typeMatch = !this.visibleResources() || this.visibleResources() == "db";
            var isMatch = (!filter || (db.name.toLowerCase().indexOf(filterLower) >= 0)) && db.name != '<system>' && typeMatch;
            db.isVisible(isMatch);
        });
        this.databases().map((db: database) => db.isChecked(!db.isVisible() ? false : db.isChecked()));

        this.fileSystems().forEach(d=> {
            var typeMatch = !this.visibleResources() || this.visibleResources() == "fs";
            var isMatch = (!filter || (d.name.toLowerCase().indexOf(filterLower) >= 0)) && typeMatch;
            d.isVisible(isMatch);
        });

        this.fileSystems().map((fs: filesystem) => fs.isChecked(!fs.isVisible() ? false : fs.isChecked()));
    }

    getDocumentsUrl(db: database) {
        return appUrl.forDocuments(null, db);
    }

    getFilesystemFilesUrl(fs: filesystem) {
        return appUrl.forFilesystemFiles(fs);
    }

    selectResource(rs: resource, activateResource: boolean = true) {
        if (this.optionsClicked() == false) {
            if (activateResource) {
                rs.activate();
            }
            this.selectedResource(rs);
        }

        this.optionsClicked(false);
    }

    deleteSelectedResources(resources: Array<resource>) {
        if (resources.length > 0) {
            require(["viewmodels/deleteResourceConfirm"], deleteResourceConfirm => {
                var confirmDeleteViewModel = new deleteResourceConfirm(resources);

                confirmDeleteViewModel.deleteTask.done((deletedResources: Array<resource>) => {
                    if (resources.length == 1) {
                        this.onResourceDeleted(resources[0]);
                    } else {
                        deletedResources.forEach(rs => this.onResourceDeleted(rs));
                    }
                });

                app.showDialog(confirmDeleteViewModel);
            });
        }
    }

    private onResourceDeleted(rs: resource) {
        if (rs.type == database.type) {
            var databaseInArray = this.databases.first((db: database) => db.name == rs.name);

            if (!!databaseInArray) {
                this.databases.remove(databaseInArray);
            }
        } else if (rs.type == filesystem.type) {
            var fileSystemInArray = this.fileSystems.first((fs: filesystem) => fs.name == rs.name);

            if (!!fileSystemInArray) {
                this.fileSystems.remove(fileSystemInArray);
            }
        } else {
            //TODO: counters
        }

        if ((this.resources().length > 0) && (this.resources().contains(this.selectedResource()) === false)) {
            this.selectResource(this.resources().first());
        }
    }

    deleteCheckedResources() {
        var checkedResources: resource[] = this.resources().filter((rs: resource) => rs.isChecked());
        this.deleteSelectedResources(checkedResources);
    }

    toggleSelectAll() {
        var check = true;

        if (this.isAnyResourceSelected()) {
            check = false;
        }

        for (var i = 0; i < this.resources().length; i++) {
            var rs: resource = this.resources()[i];
            if (rs.isDatabase() && (<database>rs).isSystem) {
                rs.isChecked(false);
                continue;
            }
            if (rs.isVisible()) {   
                rs.isChecked(check);
            }
        }
    }

    toggleCheckedResources() {
        var checkedResources: resource[] = this.resources().filter((rs: resource) => rs.isChecked());
        this.toggleSelectedResources(checkedResources);
    }

    toggleSelectedResources(resources: resource[]) {
        if (resources.length > 0) {
            var action = !resources[0].disabled();

            require(["viewmodels/disableResourceToggleConfirm"], disableResourceToggleConfirm => {
                var disableDatabaseToggleViewModel = new disableResourceToggleConfirm(resources);

                disableDatabaseToggleViewModel.disableToggleTask
                    .done((toggledResources: resource[]) => {
                        if (resources.length == 1) {
                            this.onResourceDisabledToggle(resources[0], action);
                        } else {
                            toggledResources.forEach(rs => {
                                this.onResourceDisabledToggle(rs, action);
                            });
                        }
                    });

                app.showDialog(disableDatabaseToggleViewModel);
            });
        }
    }

    private onResourceDisabledToggle(rs: resource, action: boolean) {
        if (!!rs) {
            rs.disabled(action);
            rs.isChecked(false);
        }
    }

    disableDatabaseIndexing(db: database) {
        var action = !db.indexingDisabled();
        var actionText = db.indexingDisabled() ? "Enable" : "Disable"; 
        var message = this.confirmationMessage(actionText + " indexing?", "Are you sure?");
        
        message.done(() => {
            require(["commands/disableIndexingCommand"], disableIndexingCommand => {
                var task = new disableIndexingCommand(db.name, action).execute();
                task.done(() => db.indexingDisabled(action));
            });
        });
    }

    toggleRejectDatabaseClients(db: database) {
        var action = !db.rejectClientsMode();
        var actionText = action ? "reject clients mode" : "accept clients mode";
        var message = this.confirmationMessage("Switch to " + actionText, "Are you sure?");
        message.done(() => {
            require(["commands/toggleRejectDatabaseClients"], toggleRejectDatabaseClients => {
                var task = new toggleRejectDatabaseClients(db.name, action).execute();
                task.done(() => db.rejectClientsMode(action));
            });
        });
    }
    
    navigateToAdminSettings() {
        this.navigate(this.appUrls.adminSettings());
        shell.disconnectFromResourceChangesApi();
    }

    dismissAlert(uniqueKey: string) {
        new dismissAlertCommand(appUrl.getSystemDatabase(), uniqueKey).execute();
    }

    urlForAlert(alert: alert) {
        var index = this.alerts().indexOf(alert);
        return appUrl.forAlerts(appUrl.getSystemDatabase()) + "&item=" + index;
	}

    newResource() {
        require(["viewmodels/createResource"], createResource => {
            var createResourceViewModel = new createResource(this.databases, this.fileSystems, license.licenseStatus);
            createResourceViewModel.createDatabasePart
                .creationTask
                .done((databaseName: string, bundles: string[], databasePath: string, databaseLogs: string, databaseIndexes: string, storageEngine: string, incrementalBackup: boolean
                    , alertTimeout: string, alertRecurringTimeout: string) => {
                    var settings = {
                        "Raven/ActiveBundles": bundles.join(";")
                    };
                    if (storageEngine) {
                        settings["Raven/StorageTypeName"] = storageEngine;
                    }
                    if (incrementalBackup) {
                        if (storageEngine === "esent") {
                            settings["Raven/Esent/CircularLog"] = "false"
                        } else {
                            settings["Raven/Voron/AllowIncrementalBackups"] = "true"
                        }
                    }
                    if (alertTimeout !== "") {
                        settings["Raven/IncrementalBackup/AlertTimeoutHours"] = alertTimeout;
                    }
                    if (alertRecurringTimeout !== "") {
                        settings["Raven/IncrementalBackup/RecurringAlertTimeoutDays"] = alertRecurringTimeout;
                    }
                    settings["Raven/DataDir"] = (!this.isEmptyStringOrWhitespace(databasePath)) ? databasePath : "~/" + databaseName;
                    if (!this.isEmptyStringOrWhitespace(databaseLogs)) {
                        settings["Raven/Esent/LogsPath"] = databaseLogs;
                    }
                    if (!this.isEmptyStringOrWhitespace(databaseIndexes)) {
                        settings["Raven/IndexStoragePath"] = databaseIndexes;
                    }

                    this.showDbCreationAdvancedStepsIfNecessary(databaseName, bundles, settings);
                });

            createResourceViewModel.createFilesystemPart
                 .creationTask
                .done((fsSettings: fileSystemSettingsDto) => this.showCreationAdvancedStepsIfNecessary(fsSettings));

            app.showDialog(createResourceViewModel);
        });
    }

    showDbCreationAdvancedStepsIfNecessary(databaseName: string, bundles: string[], settings: {}) {
        var securedSettings = {};
        var savedKey;

        var encryptionDeferred = $.Deferred();

        if (bundles.contains("Encryption")) {
            require(["viewmodels/createEncryption"], createEncryption => {
                var createEncryptionViewModel = new createEncryption();
                createEncryptionViewModel
                    .creationEncryption
                    .done((key: string, encryptionAlgorithm: string, encryptionBits: string, isEncryptedIndexes: string) => {
                        savedKey = key;
                        securedSettings = {
                            'Raven/Encryption/Key': key,
                            'Raven/Encryption/Algorithm': this.getEncryptionAlgorithmFullName(encryptionAlgorithm),
                            'Raven/Encryption/KeyBitsPreference': encryptionBits,
                            'Raven/Encryption/EncryptIndexes': isEncryptedIndexes
                        };
                        encryptionDeferred.resolve(securedSettings);
                    });
                app.showDialog(createEncryptionViewModel);
            });
        } else {
            encryptionDeferred.resolve();
        }

        encryptionDeferred.done(() => {
            require(["commands/createDatabaseCommand"], createDatabaseCommand => {
                new createDatabaseCommand(databaseName, settings, securedSettings)
                    .execute()
                    .done(() => {
                        var newDatabase = this.addNewDatabase(databaseName, bundles);
                        this.selectResource(newDatabase);

                        var encryptionConfirmationDialogPromise = $.Deferred();
                        if (!jQuery.isEmptyObject(securedSettings)) {
                            require(["viewmodels/createEncryptionConfirmation"], createEncryptionConfirmation => {
                                var createEncryptionConfirmationViewModel = new createEncryptionConfirmation(savedKey);
                                createEncryptionConfirmationViewModel.dialogPromise.done(() => encryptionConfirmationDialogPromise.resolve());
                                createEncryptionConfirmationViewModel.dialogPromise.fail(() => encryptionConfirmationDialogPromise.reject());
                                app.showDialog(createEncryptionConfirmationViewModel);
                            });
                        } else {
                            encryptionConfirmationDialogPromise.resolve();
                        }

                        this.createDefaultSettings(newDatabase, bundles).always(() => {
                            if (bundles.contains("Quotas") || bundles.contains("Versioning") || bundles.contains("SqlReplication")) {
                                encryptionConfirmationDialogPromise.always(() => {
                                    require(["viewmodels/databaseSettingsDialog"], databaseSettingsDialog => {
                                        var settingsDialog = new databaseSettingsDialog(bundles);
                                        app.showDialog(settingsDialog);
                                    });
                                });
                            }
                        });
                    });
            });
        });
    }

    private addNewDatabase(databaseName: string, bundles: string[]): database {
        var foundDatabase = this.databases.first((db: database) => db.name == databaseName);

        if (!foundDatabase) {
            var newDatabase = new database(databaseName, false, bundles);
            this.databases.unshift(newDatabase);
            this.filterResources();
            return newDatabase;
        }
        return foundDatabase;
    }

    private createDefaultSettings(db: database, bundles: Array<string>): JQueryPromise<any> {
        var deferred = $.Deferred();
        require(["commands/createDefaultSettingsCommand"], createDefaultSettingsCommand => {
            new createDefaultSettingsCommand(db, bundles).execute()
                .always(() => deferred.resolve());
        });
        return deferred;
    }

    private isEmptyStringOrWhitespace(str: string) {
        return !$.trim(str);
    }

    private getEncryptionAlgorithmFullName(encrytion: string) {
        var fullEncryptionName: string = null;
        switch (encrytion) {
            case "DES":
                fullEncryptionName = "System.Security.Cryptography.DESCryptoServiceProvider, mscorlib";
                break;
            case "RC2":
                fullEncryptionName = "System.Security.Cryptography.RC2CryptoServiceProvider, mscorlib";
                break;
            case "Rijndael":
                fullEncryptionName = "System.Security.Cryptography.RijndaelManaged, mscorlib";
                break;
            default: //case "Triple DES":
                fullEncryptionName = "System.Security.Cryptography.TripleDESCryptoServiceProvider, mscorlib";
        }
        return fullEncryptionName;
    }

    showCreationAdvancedStepsIfNecessary(fsSettings: fileSystemSettingsDto) {
        require(["commands/filesystem/createFilesystemCommand"], createFileSystemCommand => {
            new createFileSystemCommand(fsSettings).execute()
                .done(() => {
                    var newFileSystem = this.addNewFileSystem(fsSettings.name);
                    this.selectResource(newFileSystem);
                });
        });
    }

    private addNewFileSystem(fileSystemName: string): filesystem {
        var foundFileSystem = this.fileSystems.first((fs: filesystem) => fs.name == fileSystemName);

        if (!foundFileSystem) {
            var newFileSystem = new filesystem(fileSystemName);
            this.fileSystems.unshift(newFileSystem);
            this.filterResources();
            return newFileSystem;
        }
        return foundFileSystem;
    }

}

export = resources;