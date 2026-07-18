#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>
#import <AppKit/AppKit.h>
#import <CoreAudio/CoreAudio.h>

typedef void (*SefirahNotifyActionCallback)(const char *notificationId, const char *actionId, const char *userInfoJson);

static SefirahNotifyActionCallback g_actionCallback = NULL;
static id g_delegate = nil;
static BOOL g_notificationsReady = NO;

@interface SefirahNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end

@implementation SefirahNotificationDelegate

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
       willPresentNotification:(UNNotification *)notification
         withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completionHandler
{
    completionHandler(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList | UNNotificationPresentationOptionSound);
}

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
didReceiveNotificationResponse:(UNNotificationResponse *)response
         withCompletionHandler:(void (^)(void))completionHandler
{
    if (g_actionCallback != NULL)
    {
        NSString *identifier = response.notification.request.identifier ?: @"";
        NSString *actionId = response.actionIdentifier ?: @"";
        if ([actionId isEqualToString:UNNotificationDefaultActionIdentifier])
            actionId = @"default";
        if ([actionId isEqualToString:UNNotificationDismissActionIdentifier])
            actionId = @"dismiss";

        NSDictionary *userInfo = response.notification.request.content.userInfo ?: @{};
        NSData *jsonData = [NSJSONSerialization dataWithJSONObject:userInfo options:0 error:nil];
        NSString *json = jsonData ? [[NSString alloc] initWithData:jsonData encoding:NSUTF8StringEncoding] : @"{}";

        g_actionCallback(identifier.UTF8String, actionId.UTF8String, json.UTF8String);
    }
    completionHandler();
}

@end

static NSString *SefirahNSString(const char *utf8)
{
    if (utf8 == NULL)
        return @"";
    return [NSString stringWithUTF8String:utf8] ?: @"";
}

static BOOL SefirahIsPackagedApp(void)
{
    NSString *path = [NSBundle mainBundle].bundlePath;
    return path.length > 0 && [path.pathExtension.lowercaseString isEqualToString:@"app"];
}

void sefirah_notify_set_action_callback(SefirahNotifyActionCallback callback)
{
    g_actionCallback = callback;
}

int sefirah_notify_is_available(void)
{
    return g_notificationsReady ? 1 : 0;
}

void sefirah_notify_initialize(void)
{
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        if (!SefirahIsPackagedApp())
        {
            g_notificationsReady = NO;
            return;
        }

        @try
        {
            g_delegate = [[SefirahNotificationDelegate alloc] init];
            UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
            center.delegate = g_delegate;
            g_notificationsReady = YES;
        }
        @catch (NSException *ex)
        {
            NSLog(@"Sefirah: UserNotifications init failed: %@", ex);
            g_notificationsReady = NO;
        }
    });
}

void sefirah_notify_request_authorization(void)
{
    sefirah_notify_initialize();
    if (!g_notificationsReady)
        return;

    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    dispatch_semaphore_t sem = dispatch_semaphore_create(0);
    [center requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound | UNAuthorizationOptionBadge)
                          completionHandler:^(__unused BOOL granted, __unused NSError * _Nullable error) {
        dispatch_semaphore_signal(sem);
    }];
    dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(5 * NSEC_PER_SEC)));
}

void sefirah_notify_register_category(const char *categoryId, const char *actionsJson)
{
    sefirah_notify_initialize();
    if (!g_notificationsReady)
        return;

    NSString *categoryIdentifier = SefirahNSString(categoryId);
    NSString *json = SefirahNSString(actionsJson);
    NSData *data = [json dataUsingEncoding:NSUTF8StringEncoding];
    NSArray *items = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    if (![items isKindOfClass:[NSArray class]])
        return;

    NSMutableArray<UNNotificationAction *> *actions = [NSMutableArray array];
    for (id item in items)
    {
        if (![item isKindOfClass:[NSDictionary class]])
            continue;
        NSString *actionId = item[@"id"];
        NSString *title = item[@"title"];
        if (actionId.length == 0 || title.length == 0)
            continue;
        UNNotificationActionOptions options = UNNotificationActionOptionNone;
        if ([item[@"foreground"] boolValue])
            options |= UNNotificationActionOptionForeground;
        if ([item[@"destructive"] boolValue])
            options |= UNNotificationActionOptionDestructive;
        [actions addObject:[UNNotificationAction actionWithIdentifier:actionId title:title options:options]];
    }

    UNNotificationCategory *category =
        [UNNotificationCategory categoryWithIdentifier:categoryIdentifier
                                               actions:actions
                                     intentIdentifiers:@[]
                                               options:UNNotificationCategoryOptionCustomDismissAction];

    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    [center getNotificationCategoriesWithCompletionHandler:^(NSSet<UNNotificationCategory *> *existing) {
        NSMutableSet *filtered = [NSMutableSet set];
        for (UNNotificationCategory *c in existing)
        {
            if (![c.identifier isEqualToString:categoryIdentifier])
                [filtered addObject:c];
        }
        [filtered addObject:category];
        [center setNotificationCategories:filtered];
    }];
}

void sefirah_notify_show(const char *identifier,
                         const char *title,
                         const char *body,
                         const char *categoryId,
                         const char *userInfoJson)
{
    sefirah_notify_initialize();
    if (!g_notificationsReady)
        return;

    UNMutableNotificationContent *content = [[UNMutableNotificationContent alloc] init];
    content.title = SefirahNSString(title);
    content.body = SefirahNSString(body);
    content.sound = [UNNotificationSound defaultSound];

    NSString *category = SefirahNSString(categoryId);
    if (category.length > 0)
        content.categoryIdentifier = category;

    NSString *infoJson = SefirahNSString(userInfoJson);
    NSData *infoData = [infoJson dataUsingEncoding:NSUTF8StringEncoding];
    id infoObj = infoData ? [NSJSONSerialization JSONObjectWithData:infoData options:0 error:nil] : nil;
    if ([infoObj isKindOfClass:[NSDictionary class]])
        content.userInfo = infoObj;

    NSString *requestId = SefirahNSString(identifier);
    if (requestId.length == 0)
        requestId = [[NSUUID UUID] UUIDString];

    UNNotificationRequest *request =
        [UNNotificationRequest requestWithIdentifier:requestId content:content trigger:nil];

    [[UNUserNotificationCenter currentNotificationCenter]
        addNotificationRequest:request
         withCompletionHandler:^(__unused NSError * _Nullable error) {}];
}

void sefirah_notify_remove(const char *identifier)
{
    if (!g_notificationsReady)
        return;
    NSString *requestId = SefirahNSString(identifier);
    if (requestId.length == 0)
        return;
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    [center removeDeliveredNotificationsWithIdentifiers:@[requestId]];
    [center removePendingNotificationRequestsWithIdentifiers:@[requestId]];
}

void sefirah_notify_clear_all(void)
{
    if (!g_notificationsReady)
        return;
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    [center removeAllDeliveredNotifications];
    [center removeAllPendingNotificationRequests];
}

void sefirah_window_hide(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        for (NSWindow *window in [NSApplication sharedApplication].windows)
        {
            if (window.isVisible)
                [window orderOut:nil];
        }
    });
}

void sefirah_window_show(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        [NSApp activateIgnoringOtherApps:YES];
        for (NSWindow *window in [NSApplication sharedApplication].windows)
            [window makeKeyAndOrderFront:nil];
    });
}

int sefirah_window_is_visible(void)
{
    __block int visible = 0;
    void (^check)(void) = ^{
        for (NSWindow *window in [NSApplication sharedApplication].windows)
        {
            if (window.isVisible)
            {
                visible = 1;
                break;
            }
        }
    };

    if ([NSThread isMainThread])
        check();
    else
        dispatch_sync(dispatch_get_main_queue(), check);

    return visible;
}

/// Writes the default Core Audio output device name into buffer (UTF-8, null-terminated).
/// Returns 1 on success, 0 on failure.
int sefirah_audio_get_default_output_name(char *buffer, int bufferSize)
{
    if (buffer == NULL || bufferSize <= 1)
        return 0;

    buffer[0] = '\0';

    AudioDeviceID deviceId = kAudioObjectUnknown;
    UInt32 size = sizeof(deviceId);
    AudioObjectPropertyAddress address = {
        kAudioHardwarePropertyDefaultOutputDevice,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain
    };

    OSStatus status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, &deviceId);
    if (status != noErr || deviceId == kAudioObjectUnknown)
        return 0;

    address.mSelector = kAudioObjectPropertyName;
    CFStringRef nameRef = NULL;
    size = sizeof(nameRef);
    status = AudioObjectGetPropertyData(deviceId, &address, 0, NULL, &size, &nameRef);
    if (status != noErr || nameRef == NULL)
        return 0;

    Boolean ok = CFStringGetCString(nameRef, buffer, (CFIndex)bufferSize, kCFStringEncodingUTF8);
    CFRelease(nameRef);
    return ok ? 1 : 0;
}
