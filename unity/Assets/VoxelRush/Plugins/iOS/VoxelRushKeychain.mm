// AUTH-002 refresh token in the iOS Keychain; bound by KeychainBackend in SecureTokenStore.cs.
// Plain CoreFoundation so it builds the same with or without ARC.
#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <stdlib.h>
#include <string.h>

static const char *const kVoxelRushService = "com.doubletstudio.voxelrush";

static CFMutableDictionaryRef VoxelRushQuery(const char *key)
{
    CFMutableDictionaryRef query = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFStringRef service = CFStringCreateWithCString(kCFAllocatorDefault, kVoxelRushService, kCFStringEncodingUTF8);
    CFStringRef account = CFStringCreateWithCString(kCFAllocatorDefault, key, kCFStringEncodingUTF8);
    CFDictionarySetValue(query, kSecClass, kSecClassGenericPassword);
    CFDictionarySetValue(query, kSecAttrService, service);
    CFDictionarySetValue(query, kSecAttrAccount, account);
    CFRelease(service);
    CFRelease(account);
    return query;
}

extern "C" char *VoxelRushKeychainRead(const char *key)
{
    if (key == NULL)
    {
        return NULL;
    }

    CFMutableDictionaryRef query = VoxelRushQuery(key);
    CFDictionarySetValue(query, kSecReturnData, kCFBooleanTrue);
    CFDictionarySetValue(query, kSecMatchLimit, kSecMatchLimitOne);
    CFTypeRef result = NULL;
    OSStatus status = SecItemCopyMatching(query, &result);
    CFRelease(query);
    if (status != errSecSuccess || result == NULL)
    {
        return NULL;
    }

    CFDataRef data = (CFDataRef)result;
    CFIndex length = CFDataGetLength(data);
    // The marshaller frees a returned string with free(), so it must come from malloc.
    char *copy = (char *)malloc((size_t)length + 1);
    if (copy != NULL)
    {
        memcpy(copy, CFDataGetBytePtr(data), (size_t)length);
        copy[length] = '\0';
    }

    CFRelease(result);
    return copy;
}

extern "C" bool VoxelRushKeychainWrite(const char *key, const char *value)
{
    if (key == NULL || value == NULL)
    {
        return false;
    }

    CFDataRef data = CFDataCreate(kCFAllocatorDefault, (const UInt8 *)value, (CFIndex)strlen(value));
    CFMutableDictionaryRef attributes = CFDictionaryCreateMutable(kCFAllocatorDefault, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFDictionarySetValue(attributes, kSecValueData, data);
    CFDictionarySetValue(attributes, kSecAttrAccessible, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);

    CFMutableDictionaryRef query = VoxelRushQuery(key);
    OSStatus status = SecItemUpdate(query, attributes);
    if (status == errSecItemNotFound)
    {
        CFDictionarySetValue(query, kSecValueData, data);
        CFDictionarySetValue(query, kSecAttrAccessible, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
        status = SecItemAdd(query, NULL);
    }

    CFRelease(query);
    CFRelease(attributes);
    CFRelease(data);
    return status == errSecSuccess;
}

extern "C" void VoxelRushKeychainDelete(const char *key)
{
    if (key == NULL)
    {
        return;
    }

    CFMutableDictionaryRef query = VoxelRushQuery(key);
    SecItemDelete(query);
    CFRelease(query);
}
