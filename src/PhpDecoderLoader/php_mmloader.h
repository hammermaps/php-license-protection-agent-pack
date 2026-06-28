#ifndef PHP_MMLOADER_H
#define PHP_MMLOADER_H

extern zend_module_entry mmloader_module_entry;
#define phpext_mmloader_ptr &mmloader_module_entry

#define PHP_MMLOADER_VERSION "0.1.0"

/* MMENC1 container format version bounds.
 * When the encoder produces a file with formatVersion > MMLOADER_FORMAT_VERSION_MAX
 * the loader rejects it with a descriptive error, preventing silent mismatches.
 * Bump MMLOADER_FORMAT_VERSION_MAX when the loader is updated to understand a
 * new format version; bump the encoder's FormatVersion when the format changes. */
#define MMLOADER_FORMAT_VERSION_MIN 1
#define MMLOADER_FORMAT_VERSION_MAX 1

#endif
