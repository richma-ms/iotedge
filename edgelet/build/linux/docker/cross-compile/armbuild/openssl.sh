# Adapted from https://github.com/japaric/cross
set -ex

main() {
    local SHLIB_VERSION_NUMBER=1.0.2 
    local SHLIB_MAJOR=1 
    local SHLIB_MINOR=0.2 
    local version=${SHLIB_VERSION_NUMBER}m
    local os=$1 \
    local triple=$2
    local sysroot=$3

    local dependencies=(
        ca-certificates
        curl
        m4
        make
        perl
    )

    # NOTE cross toolchain must be already installed
    apt-get update
    for dep in ${dependencies[@]}; do
        if ! dpkg -L $dep; then
            apt-get install --no-install-recommends -y $dep
        fi
    done

    td=$(mktemp -d)

    pushd $td
    curl https://www.openssl.org/source/openssl-$version.tar.gz | \
        tar --strip-components=1 -xz
    AR=${triple}ar CC=${triple}gcc ./Configure \
      --prefix=${sysroot}/usr \
      --openssldir=${sysroot}/usr \
      shared \
      no-asm \
      $os \
      -fPIC \
      ${@:4}
    make SHLIB_VERSION_NUMBER=${SHLIB_VERSION_NUMBER} SHLIB_MAJOR=${SHLIB_MAJOR} SHLIB_MINOR=${SHLIB_MINOR} -j$(nproc)
    make SHLIB_VERSION_NUMBER=${SHLIB_VERSION_NUMBER} SHLIB_MAJOR=${SHLIB_MAJOR} SHLIB_MINOR=${SHLIB_MINOR} install_sw
    
    # clean up

    popd

    rm -rf $td
    rm $0
}

main "${@}"
