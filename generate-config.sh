#!/bin/sh -e

# rclone config create `name` `type` [`key` `value`]* [flags]

if [[ -z ${ACCESS_KEY_ID} ]];
then
    echo "TARGET env var is not set so we use the default value (/data)"
    ACCESS_KEY_ID=/data
else
    echo "TARGET env var is set"
fi
rclone config create remote 

rclone config create remote s3 provider 'other' access_key_id 'AKIASCYBBAI3MEROKYEA' secret_access_key '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' region 'eu-west-1' acl 'public-read'