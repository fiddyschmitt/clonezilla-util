# clonezilla-util
A tool for working with Clonezilla images.

## Where to download
Releases can be found over in the [releases](https://github.com/fiddyschmitt/clonezilla-util/releases) section.

## Extract a single file from a Clonezilla Image

Run the following command:

`clonezilla-util extract-partition-image --input <clonzilla folder> --output <folder to extract to>`

The program creates a file for each partition in the Clonezilla archive.

<div style="float:left;margin:100px 50px 50px 0" markdown="1">
<kbd>
<img src="https://i.imgur.com/KWpUL0J.png" width="800">
</kbd>
</div>

If the images are extracted to an NTFS drive, they are created as sparse. Meaning they only take up the necessary space:

<img src="https://i.imgur.com/r0sepb7.png" width="300">

Now you can open the image files using 7-Zip and extract the file(s) you desire.

<img src="https://i.imgur.com/enJhShq.png" width="600">
