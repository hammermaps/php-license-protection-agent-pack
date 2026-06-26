PHP_ARG_ENABLE(mmloader, whether to enable MMProtect Loader,
[  --enable-mmloader        Enable MMProtect PHP loader])

if test "$PHP_MMLOADER" != "no"; then
  PHP_CHECK_LIBRARY(crypto, EVP_EncryptInit_ex,
    [PHP_ADD_LIBRARY(crypto, 1, MMLOADER_SHARED_LIBADD)],
    [AC_MSG_ERROR([libcrypto not found — install libssl-dev])])

  PHP_CHECK_LIBRARY(curl, curl_global_init,
    [PHP_ADD_LIBRARY(curl, 1, MMLOADER_SHARED_LIBADD)],
    [AC_MSG_ERROR([libcurl not found — install libcurl4-openssl-dev])])

  PHP_ADD_INCLUDE($ext_srcdir/vendor/cjson)
  PHP_ADD_INCLUDE($ext_srcdir/vendor/lz4)

  PHP_NEW_EXTENSION(mmloader, mmloader.c vendor/cjson/cJSON.c vendor/lz4/lz4_decompress.c, $ext_shared)
  PHP_SUBST(MMLOADER_SHARED_LIBADD)
fi
