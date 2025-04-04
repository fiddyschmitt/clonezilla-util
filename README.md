# clonezilla-util
Extract individual files from a Clonezilla archive, without extracting the full archive.

## Supported formats
    Clonezilla archives
    Partclone images
    Drive images     (eg. sda.img)
    Partition images (eg. sda1.img)
    Compressed versions of the above (bzip2, GZip, LZ4, LZip, xz, Zstandard) 

## Where to download
Releases can be found over in the [releases](https://github.com/fiddyschmitt/clonezilla-util/releases) section.
<br />
<br />

## Mount a Clonezilla archive in Windows

First, install [this version](https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000) of the Dokan Driver. This is required to create a Virtual Drive.

Now run the following command:

`clonezilla-util.exe mount --input <clonezilla folder> --mount L:\`

A virtual drive is created, containing all the files in the Clonezilla archive:

<div style="float:left;margin:100px 50px 50px 0" markdown="1">
<kbd>
<img src="https://user-images.githubusercontent.com/15338956/149607919-96f95a2d-c492-4e5a-91ec-a2626da77a17.png" width="800">
</kbd>
</div>

<br />
<br />
<br />

## Mount a Clonezilla archive as partition image files

First, install [this version](https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000) of the Dokan Driver. This is required to create a Virtual Drive.

Now run the following command:

`clonezilla-util.exe mount-as-image-files --input <clonzilla folder> --mount L:\`

A virtual drive is created, containing a file for each partition:

<div style="float:left;margin:100px 50px 50px 0" markdown="1">
<kbd>
<img src="https://user-images.githubusercontent.com/15338956/149607394-31ccf24b-a215-4073-9d3c-abb82126c628.png" width="800">
</kbd>
</div>

<br />
<br />

You can open them using 7-Zip, and extract individual files:

<img src="https://i.imgur.com/enJhShq.png" width="600">

<br />
<br />
<br />

# Advanced features

## Mount raw images

Run the following command:

`clonezilla-util.exe mount --input "sda.img" --mount L:\`
<br />
<br />

## Extract partition images from Clonezilla archive (Dokan not required)

Run the following command:

`clonezilla-util.exe extract-partition-image --input <clonzilla folder> --output <folder to extract to>`

The program creates a file for each partition in the Clonezilla archive.

<div style="float:left;margin:100px 50px 50px 0" markdown="1">
<kbd>
<img src="https://i.imgur.com/KWpUL0J.png" width="800">
</kbd>
</div>

<br />
<br />

If the images are extracted to an NTFS drive, they are created as sparse. Meaning they only take up the necessary space:

<img src="https://i.imgur.com/r0sepb7.png" width="300">
<br />
<br />
<br />

# Thanks

A special thanks to Roberto for creating [gztool](https://github.com/circulosmeos/gztool). It allows the gz files in the Clonezilla archive to be read randomly, which gz doesn't natively support.

Thanks to the [Dokan team](https://github.com/dokan-dev/dokan-dotnet). Dokan is what is used to create the virtual drive.

Thanks to Igor for [7-Zip](https://sourceforge.net/projects/sevenzip/). His versatile tool inspects the contents of the image files, which can be in many formats (eg. NTFS, FAT, etc.)

Thanks to Steven for [Clonezilla](https://github.com/stevenshiau/clonezilla), the backup tool used by millions.
